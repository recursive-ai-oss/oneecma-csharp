namespace RecursiveAI.OneEcma.RegExp;

/// <summary>A class evaluated to code points plus (for v-mode) multi-character strings; ECMA-262 22.2.2 CharSet.</summary>
internal readonly record struct ClassValue(RangeSet Chars, string[] Strings)
{
    internal static readonly ClassValue Empty = new(RangeSet.Empty, []);
}

/// <summary>Turns a parsed pattern into bytecode, resolving classes under the flags in effect at each node.</summary>
internal sealed class RegExpCompiler
{
    private static readonly RangeSet Digits = RangeSet.Range('0', '9');

    // WhiteSpace and LineTerminator code points (12.2, 12.3).
    private static readonly RangeSet Whitespace = RangeSet.FromRanges(
    [
        (9, 13), (0x20, 0x20), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000), (0xFEFF, 0xFEFF),
    ]);

    private readonly List<int> _code = [];
    private readonly List<CharClass> _classes = [];
    private readonly List<StringTrie> _tries = [];
    private readonly List<int[]> _backrefs = [];
    private readonly bool _unicode; // u or v
    private readonly bool _v;
    private readonly int _groupCount;
    private readonly int[] _groupTemp;
    private int _regCount;
    private bool _usesIgnoreCase;

    private RegExpCompiler(int groupCount, bool unicode, bool unicodeSets)
    {
        _groupCount = groupCount;
        _unicode = unicode || unicodeSets;
        _v = unicodeSets;
        _regCount = 2 * (groupCount + 1);
        _groupTemp = new int[groupCount + 1];
        Array.Fill(_groupTemp, -1);
    }

    internal static CompiledProgram Compile(ParseResult parsed, ModeFlags mode, bool unicode, bool unicodeSets)
    {
        var c = new RegExpCompiler(parsed.GroupCount, unicode, unicodeSets);
        c.Emit(parsed.Root!, mode, false);
        c._code.Add(Op.Match);
        return new CompiledProgram([.. c._code], [.. c._classes], [.. c._tries], [.. c._backrefs], c._regCount, parsed.GroupCount, c._unicode, c._usesIgnoreCase);
    }

    private int NewReg() => _regCount++;

    private int TempFor(int group) => _groupTemp[group] >= 0 ? _groupTemp[group] : (_groupTemp[group] = NewReg());

    private int Here => _code.Count;

    private void Add(params int[] ops) => _code.AddRange(ops);

    // A pattern may hold a lone surrogate (as a raw code unit or a \u escape); char.ConvertFromUtf32 rejects those.
    private static void AppendCodePoint(System.Text.StringBuilder sb, int cp)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append((char)cp);
        }
        else
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
    }

    private int AddJump(int op)
    {
        _code.Add(op);
        _code.Add(-1);
        return _code.Count - 1;
    }

    private void Patch(int operandIndex) => _code[operandIndex] = _code.Count;

    private static int Fl(bool back, bool ic, bool dotAll = false) => (back ? Op.FlagBack : 0) | (ic ? Op.FlagIgnoreCase : 0) | (dotAll ? Op.FlagDotAll : 0);

    private void Emit(Node node, ModeFlags m, bool back)
    {
        bool ic = (m & ModeFlags.IgnoreCase) != 0;
        switch (node)
        {
            case EmptyNode:
                break;
            case CharNode c:
                EmitChar(c.Cp, ic, back);
                break;
            case AnyNode:
                Add(Op.Any, Fl(back, false, (m & ModeFlags.DotAll) != 0));
                break;
            case ClassNode cn:
                EmitClass(cn, m, back);
                break;
            case EscapeNode en:
                EmitEscapeAtom(en, m, back);
                break;
            case AssertNode a:
                EmitAssertion(a, m);
                break;
            case GroupNode g:
            {
                int tmp = TempFor(g.Index);
                Add(Op.Mark, tmp);
                Emit(g.Body, m, back);
                Add(Op.EndGroup, g.Index, tmp, back ? 1 : 0);
                break;
            }

            case ModifierNode mod:
            {
                ModeFlags m2 = m;
                // 22.2.2.7.4 UpdateModifiers: remove wins over add for the same letter.
                m2 = (mod.Remove & ModeFlags.IgnoreCase) != 0 ? m2 & ~ModeFlags.IgnoreCase : (mod.Add & ModeFlags.IgnoreCase) != 0 ? m2 | ModeFlags.IgnoreCase : m2;
                m2 = (mod.Remove & ModeFlags.Multiline) != 0 ? m2 & ~ModeFlags.Multiline : (mod.Add & ModeFlags.Multiline) != 0 ? m2 | ModeFlags.Multiline : m2;
                m2 = (mod.Remove & ModeFlags.DotAll) != 0 ? m2 & ~ModeFlags.DotAll : (mod.Add & ModeFlags.DotAll) != 0 ? m2 | ModeFlags.DotAll : m2;
                Emit(mod.Body, m2, back);
                break;
            }

            case LookNode l:
            {
                int endOperand = -1;
                _code.Add(Op.LookStart);
                _code.Add(l.Negate ? 1 : 0);
                _code.Add(-1);
                endOperand = _code.Count - 1;
                Emit(l.Body, m, l.Behind);
                Add(Op.LookEnd, l.Negate ? 1 : 0);
                Patch(endOperand);
                break;
            }

            case BackrefNode b:
                _usesIgnoreCase |= ic;
                _backrefs.Add(b.Groups);
                Add(Op.Backref, _backrefs.Count - 1, Fl(back, ic));
                break;
            case QuantNode q:
                EmitQuantifier(q, m, back);
                break;
            case AltNode alt:
            {
                var jumps = new List<int>();
                for (int i = 0; i < alt.Alternatives.Count; i++)
                {
                    if (i < alt.Alternatives.Count - 1)
                    {
                        int split = AddJump(Op.Split);
                        Emit(alt.Alternatives[i], m, back);
                        jumps.Add(AddJump(Op.Jmp));
                        Patch(split);
                    }
                    else
                    {
                        Emit(alt.Alternatives[i], m, back);
                    }
                }

                foreach (int j in jumps)
                {
                    Patch(j);
                }

                break;
            }

            case SeqNode seq:
                // MatchSequence (22.2.2.3.4): a backward sequence matches its terms last to first.
                if (back)
                {
                    for (int i = seq.Items.Count - 1; i >= 0; i--)
                    {
                        Emit(seq.Items[i], m, back);
                    }
                }
                else
                {
                    foreach (Node n in seq.Items)
                    {
                        Emit(n, m, back);
                    }
                }

                break;
            default:
                throw new InvalidOperationException("Unknown pattern node.");
        }
    }

    private void EmitChar(int cp, bool ic, bool back)
    {
        if (ic && RegExpUnicode.HasCaseVariants(cp, _unicode))
        {
            _usesIgnoreCase = true;
            Add(Op.Char, RegExpUnicode.Canonicalize(cp, _unicode), Fl(back, true));
        }
        else
        {
            Add(Op.Char, cp, Fl(back, false));
        }
    }

    private void EmitAssertion(AssertNode a, ModeFlags m)
    {
        bool ml = (m & ModeFlags.Multiline) != 0;
        switch (a.Kind)
        {
            case AssertKind.LineStart:
                Add(Op.Bol, ml ? 1 : 0);
                break;
            case AssertKind.LineEnd:
                Add(Op.Eol, ml ? 1 : 0);
                break;
            default:
            {
                var word = new CharClass(RegExpUnicode.WordCharacters(_unicode, (m & ModeFlags.IgnoreCase) != 0), false, false);
                _classes.Add(word);
                Add(Op.WordBoundary, a.Kind == AssertKind.NotWordBoundary ? 1 : 0, _classes.Count - 1);
                break;
            }
        }
    }

    // ---- classes -----------------------------------------------------------------------------------------

    private int AddClass(RangeSet set, bool invert, bool ic)
    {
        if (ic)
        {
            _usesIgnoreCase = true;
            if (!_v)
            {
                // Characters are compared after Canonicalize, so the set must hold the canonical form of its members.
                set = RegExpUnicode.AddCanonicalImages(set, _unicode);
            }
        }

        _classes.Add(new CharClass(set, invert, ic));
        return _classes.Count - 1;
    }

    private void EmitClass(ClassNode cn, ModeFlags m, bool back)
    {
        bool ic = (m & ModeFlags.IgnoreCase) != 0;
        ClassValue v = Eval(cn.Expr, m);
        if (_v)
        {
            RangeSet chars = v.Chars;
            if (cn.Negate)
            {
                // CompileCharacterClass: in v-mode the complement is taken here and Invert stays false.
                chars = RangeSet.Subtract(RegExpUnicode.AllCharacters(true, true, ic), chars);
            }

            EmitSetWithStrings(chars, v.Strings, ic, back);
            return;
        }

        Add(Op.Set, AddClass(v.Chars, cn.Negate, ic), Fl(back, ic));
    }

    private void EmitEscapeAtom(EscapeNode en, ModeFlags m, bool back)
    {
        bool ic = (m & ModeFlags.IgnoreCase) != 0;
        ClassValue v = Eval(en.Escape, m);
        EmitSetWithStrings(v.Chars, v.Strings, ic, back);
    }

    // Atom :: CharacterClass (22.2.2.7) when the class may hold strings: longest strings first, then the single
    // characters, then the empty string.
    private void EmitSetWithStrings(RangeSet chars, string[] strings, bool ic, bool back)
    {
        if (strings.Length == 0)
        {
            Add(Op.Set, AddClass(chars, false, ic), Fl(back, ic));
            return;
        }

        bool hasEmpty = Array.IndexOf(strings, string.Empty) >= 0;
        string[] multi = strings.Where(static s => s.Length > 0).ToArray();
        if (ic)
        {
            _usesIgnoreCase = true;
        }

        var ends = new List<int>();
        if (multi.Length > 0)
        {
            _tries.Add(StringTrie.Build(multi, ic, back));
            int split = AddJump(Op.Split);
            Add(Op.Strings, _tries.Count - 1, Fl(back, ic));
            ends.Add(AddJump(Op.Jmp));
            Patch(split);
        }

        if (hasEmpty)
        {
            int split = AddJump(Op.Split);
            Add(Op.Set, AddClass(chars, false, ic), Fl(back, ic));
            ends.Add(AddJump(Op.Jmp));
            Patch(split);
        }
        else
        {
            Add(Op.Set, AddClass(chars, false, ic), Fl(back, ic));
        }

        foreach (int e in ends)
        {
            Patch(e);
        }
    }

    private RangeSet AllCharacters(ModeFlags m) => RegExpUnicode.AllCharacters(_unicode, _v, (m & ModeFlags.IgnoreCase) != 0);

    private RangeSet MaybeFold(RangeSet a, ModeFlags m) => _v && (m & ModeFlags.IgnoreCase) != 0 ? RegExpUnicode.SimpleFoldSet(a) : a;

    private ClassValue Eval(ClassExpr e, ModeFlags m)
    {
        bool fold = _v && (m & ModeFlags.IgnoreCase) != 0;
        switch (e)
        {
            case ClassChar c:
                return new ClassValue(RangeSet.Single(fold ? RegExpUnicode.SimpleFold(c.Cp) : c.Cp), []);
            case ClassRange r:
                return new ClassValue(MaybeFold(RangeSet.Range(r.Lo, r.Hi), m), []);
            case ClassEscapeExpr esc:
                return EvalEscape(esc, m);
            case ClassUnion u:
            {
                RangeSet chars = RangeSet.Empty;
                HashSet<string>? strings = null;
                foreach (ClassExpr item in u.Items)
                {
                    ClassValue iv = Eval(item, m);
                    chars = RangeSet.Union(chars, iv.Chars);
                    if (iv.Strings.Length > 0)
                    {
                        (strings ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(iv.Strings);
                    }
                }

                return new ClassValue(chars, Sorted(strings));
            }

            case ClassIntersection x:
            {
                ClassValue acc = Eval(x.Items[0], m);
                for (int i = 1; i < x.Items.Count; i++)
                {
                    ClassValue b = Eval(x.Items[i], m);
                    var keep = new HashSet<string>(b.Strings, StringComparer.Ordinal);
                    acc = new ClassValue(RangeSet.Intersect(acc.Chars, b.Chars), Sorted(acc.Strings.Where(keep.Contains)));
                }

                return acc;
            }

            case ClassSubtraction s:
            {
                ClassValue acc = Eval(s.Items[0], m);
                for (int i = 1; i < s.Items.Count; i++)
                {
                    ClassValue b = Eval(s.Items[i], m);
                    var drop = new HashSet<string>(b.Strings, StringComparer.Ordinal);
                    acc = new ClassValue(RangeSet.Subtract(acc.Chars, b.Chars), Sorted(acc.Strings.Where(x => !drop.Contains(x))));
                }

                return acc;
            }

            case ClassNested n:
            {
                ClassValue inner = Eval(n.Inner, m);
                return n.Negate ? new ClassValue(RangeSet.Subtract(AllCharacters(m), inner.Chars), []) : inner;
            }

            case ClassStrings q:
            {
                var chars = new List<(int, int)>();
                var strings = new HashSet<string>(StringComparer.Ordinal);
                foreach (int[] cps in q.Strings)
                {
                    if (cps.Length == 1)
                    {
                        int cp = fold ? RegExpUnicode.SimpleFold(cps[0]) : cps[0];
                        chars.Add((cp, cp));
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (int cp in cps)
                        {
                            AppendCodePoint(sb, fold ? RegExpUnicode.SimpleFold(cp) : cp);
                        }

                        strings.Add(sb.ToString());
                    }
                }

                return new ClassValue(RangeSet.FromRanges(chars), Sorted(strings));
            }

            default:
                throw new InvalidOperationException("Unknown class expression.");
        }
    }

    private static string[] Sorted(IEnumerable<string>? strings)
    {
        if (strings is null)
        {
            return [];
        }

        string[] a = strings.Distinct(StringComparer.Ordinal).ToArray();
        Array.Sort(a, StringComparer.Ordinal);
        return a;
    }

    private ClassValue EvalEscape(ClassEscapeExpr esc, ModeFlags m)
    {
        bool ic = (m & ModeFlags.IgnoreCase) != 0;
        switch (esc.Kind)
        {
            case 'd':
                return new ClassValue(Digits, []);
            case 'D':
                return new ClassValue(RangeSet.Subtract(AllCharacters(m), Digits), []);
            case 's':
                return new ClassValue(Whitespace, []);
            case 'S':
                return new ClassValue(RangeSet.Subtract(AllCharacters(m), Whitespace), []);
            case 'w':
                return new ClassValue(MaybeFold(RegExpUnicode.WordCharacters(_unicode, ic), m), []);
            case 'W':
                return new ClassValue(RangeSet.Subtract(AllCharacters(m), MaybeFold(RegExpUnicode.WordCharacters(_unicode, ic), m)), []);
            default:
            {
                PropertyRef p = esc.Property!.Value;
                if (p.IsStrings)
                {
                    var (singles, seqs) = RegExpUnicode.GetStrings(p);
                    if (_v && ic)
                    {
                        singles = RegExpUnicode.SimpleFoldSet(singles);
                        seqs = FoldStrings(seqs);
                    }

                    return new ClassValue(singles, seqs);
                }

                RangeSet set = MaybeFold(RegExpUnicode.GetSet(p), m);
                return esc.Kind == 'p' ? new ClassValue(set, []) : new ClassValue(RangeSet.Subtract(AllCharacters(m), set), []);
            }
        }
    }

    private static string[] FoldStrings(string[] seqs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (string s in seqs)
        {
            sb.Clear();
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }

                AppendCodePoint(sb, RegExpUnicode.SimpleFold(cp));
            }

            result.Add(sb.ToString());
        }

        return Sorted(result);
    }

    // ---- quantifiers -------------------------------------------------------------------------------------

    // A node that always consumes exactly one character and has no captures can be repeated without a
    // backtrack entry per iteration.
    private bool TrySingleChar(Node body, ModeFlags m, bool back, out int kind, out int arg, out int fl)
    {
        kind = 0;
        arg = 0;
        fl = 0;
        bool ic = (m & ModeFlags.IgnoreCase) != 0;
        switch (body)
        {
            case CharNode c:
                kind = Op.KindChar;
                if (ic && RegExpUnicode.HasCaseVariants(c.Cp, _unicode))
                {
                    _usesIgnoreCase = true;
                    arg = RegExpUnicode.Canonicalize(c.Cp, _unicode);
                    fl = Fl(back, true);
                }
                else
                {
                    arg = c.Cp;
                    fl = Fl(back, false);
                }

                return true;
            case AnyNode:
                kind = Op.KindAny;
                fl = Fl(back, false, (m & ModeFlags.DotAll) != 0);
                return true;
            case ClassNode cn when !cn.CanBeEmpty:
            {
                ClassValue v = Eval(cn.Expr, m);
                if (v.Strings.Length > 0)
                {
                    return false;
                }

                RangeSet chars = v.Chars;
                bool invert = false;
                if (_v)
                {
                    if (cn.Negate)
                    {
                        chars = RangeSet.Subtract(RegExpUnicode.AllCharacters(true, true, ic), chars);
                    }
                }
                else
                {
                    invert = cn.Negate;
                }

                kind = Op.KindSet;
                arg = AddClass(chars, invert, ic);
                fl = Fl(back, ic);
                return true;
            }

            case EscapeNode en when !en.CanBeEmpty:
            {
                ClassValue v = Eval(en.Escape, m);
                if (v.Strings.Length > 0)
                {
                    return false;
                }

                kind = Op.KindSet;
                arg = AddClass(v.Chars, false, ic);
                fl = Fl(back, ic);
                return true;
            }

            default:
                return false;
        }
    }

    private void EmitQuantifier(QuantNode q, ModeFlags m, bool back)
    {
        if (q.Max == 0)
        {
            return; // RepeatMatcher: max = 0 returns c(x); the body is never tried.
        }

        if (TrySingleChar(q.Body, m, back, out int kind, out int arg, out int fl))
        {
            Add(Op.Repeat, kind, arg, fl, q.Min, q.Max, q.Greedy ? 1 : 0);
            return;
        }

        if (q.Min == 1 && q.Max == 1)
        {
            Emit(q.Body, m, back);
            return;
        }

        bool emptyCheck = q.Body.CanBeEmpty;
        int posReg = emptyCheck ? NewReg() : -1;
        int lastGroup = q.FirstGroup + q.GroupCount - 1;

        void IterationPrologue()
        {
            if (emptyCheck)
            {
                Add(Op.Mark, posReg);
            }

            if (q.GroupCount > 0)
            {
                Add(Op.Clear, q.FirstGroup, lastGroup);
            }
        }

        if (q.Min == 0 && (q.Max == int.MaxValue || q.Max == 1))
        {
            bool star = q.Max == int.MaxValue;
            int top = Here;
            int exitJump;
            if (q.Greedy)
            {
                exitJump = AddJump(Op.Split); // choice to the exit; fall into the body
                IterationPrologue();
                Emit(q.Body, m, back);
                if (emptyCheck)
                {
                    Add(Op.EmptyCheck, posReg);
                }

                if (star)
                {
                    Add(Op.Jmp, top);
                }

                Patch(exitJump);
            }
            else
            {
                int bodySplit = AddJump(Op.Split); // choice to the body; take the exit first
                exitJump = AddJump(Op.Jmp);
                Patch(bodySplit);
                IterationPrologue();
                Emit(q.Body, m, back);
                if (emptyCheck)
                {
                    Add(Op.EmptyCheck, posReg);
                }

                if (star)
                {
                    Add(Op.Jmp, top);
                }

                Patch(exitJump);
            }

            return;
        }

        // General counted loop: RepeatMatcher with a counter register.
        int cnt = NewReg();
        Add(Op.SetReg, cnt, 0);
        int loop = Here;
        _code.Add(Op.LoopSplit);
        _code.Add(cnt);
        _code.Add(q.Min);
        _code.Add(q.Max);
        _code.Add(q.Greedy ? 1 : 0);
        _code.Add(-1);
        int exitOperand = _code.Count - 1;
        IterationPrologue();
        Emit(q.Body, m, back);
        Add(Op.LoopEnd, cnt, posReg, q.Min, loop);
        Patch(exitOperand);
    }
}
