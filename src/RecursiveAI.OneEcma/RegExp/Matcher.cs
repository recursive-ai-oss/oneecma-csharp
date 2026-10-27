namespace RecursiveAI.OneEcma.RegExp;

/// <summary>Raised inside the matcher when the backtrack stack would pass its byte limit; never leaves <see cref="RegExpProgram.Exec"/>.</summary>
internal sealed class StackLimitException : Exception
{
}

/// <summary>
/// The backtracking matcher. It runs the bytecode with an explicit stack of fixed-size entries instead of host
/// recursion, so pattern and input shape cannot overflow the host stack. Every step, backtrack and consumed
/// character of a repeated atom is charged to the fuel meter, and stack growth is checked against a byte limit.
/// </summary>
internal sealed class Matcher
{
    // Backtrack entries are four ints: [tag, a, b, c].
    private const int EntryChoice = 0;       // a = pc, b = position
    private const int EntryRestore = 1;      // a = register, b = old value
    private const int EntryPosBarrier = 2;   // b = position to restore on success
    private const int EntryNegBarrier = 3;   // a = pc after the lookaround, b = position
    private const int EntryRepeatFwd = 4;    // greedy single-char loop, forward: a = next pc, b = lowest allowed end, c = current end
    private const int EntryRepeatBack = 5;   // the same inside a lookbehind: b = highest allowed end
    private const int EntryLazy = 6;         // a = pc of the Repeat op, b = position, c = iterations so far
    private const int EntryStrings = 7;      // a = pc of the Strings op, b = start position, c = longest length still allowed

    private readonly CompiledProgram _p;
    private readonly string _s;
    private readonly int _n;
    private readonly bool _unicode;
    private readonly char[]? _canon;
    private readonly FuelMeter _fuel;
    private readonly int _maxStackInts;
    private readonly CompiledProgram.Scratch _scratch;
    private readonly int[] _regs;
    private int[] _stk;
    private int _sp;
    private long _budget;
    private RegExpLimitKind _limit;

    internal Matcher(CompiledProgram program, string input, FuelMeter fuel, RegExpLimits limits, CompiledProgram.Scratch scratch)
    {
        _p = program;
        _s = input;
        _n = input.Length;
        _unicode = program.Unicode;
        _canon = program.UsesIgnoreCase ? RegExpUnicode.CanonicalizeTable(program.Unicode) : null;
        _fuel = fuel;
        long ints = Math.Clamp(limits.MaxBacktrackBytes / 4, 1024, 1L << 30);
        _maxStackInts = (int)(ints & ~3L);
        _scratch = scratch;
        if (scratch.Regs.Length < program.RegCount)
        {
            scratch.Regs = new int[program.RegCount];
        }

        _regs = scratch.Regs;
        Array.Fill(_regs, -1, 0, program.RegCount);
        _stk = scratch.Stack;
    }

    private static bool IsHigh(int c) => c is >= 0xD800 and <= 0xDBFF;

    private static bool IsLow(int c) => c is >= 0xDC00 and <= 0xDFFF;

    private static bool IsLineTerminator(int c) => c is '\n' or '\r' or 0x2028 or 0x2029;

    internal RegExpExecResult Search(int start, bool sticky)
    {
        try
        {
            _budget = 0;
            return SearchCore(start, sticky);
        }
        catch (StackLimitException)
        {
            return new RegExpExecResult(RegExpOutcome.LimitExceeded, RegExpLimitKind.Memory, null);
        }
        finally
        {
            if (_budget > 0)
            {
                _fuel.Refund(_budget);
            }

            // Do not keep a very large stack alive between calls.
            _scratch.Stack = _stk.Length > (1 << 16) ? new int[256] : _stk;
        }
    }

    private RegExpExecResult SearchCore(int start, bool sticky)
    {
        int[] code = _p.Code;
        string str = _s;
        int i = start;
        if (_unicode && i > 0 && i < _n && IsLow(str[i]) && IsHigh(str[i - 1]))
        {
            i--;
        }

        // Cheap start-position filters: a pattern that opens with ^ (without m) can only match at 0, and one that
        // opens with a literal BMP character can skip to the next occurrence of it.
        bool anchoredAtStart = code[0] == Op.Bol && code[1] == 0;
        bool literalFirst = code[0] == Op.Char && code[2] == 0 && code[1] <= 0xFFFF && !(_unicode && code[1] is >= 0xD800 and <= 0xDFFF);
        while (true)
        {
            if (anchoredAtStart && i > 0)
            {
                return new RegExpExecResult(RegExpOutcome.NoMatch, RegExpLimitKind.None, null);
            }

            if (literalFirst && !sticky)
            {
                int idx = i < _n ? str.IndexOf((char)code[1], i) : -1;
                if (idx < 0)
                {
                    return new RegExpExecResult(RegExpOutcome.NoMatch, RegExpLimitKind.None, null);
                }

                i = idx;
            }

            int r = Run(i);
            if (r == 1)
            {
                int[] caps = new int[2 * (_p.GroupCount + 1)];
                Array.Copy(_regs, caps, caps.Length);
                return new RegExpExecResult(RegExpOutcome.Match, RegExpLimitKind.None, new RegExpMatch(caps));
            }

            if (r == 2)
            {
                return new RegExpExecResult(RegExpOutcome.LimitExceeded, _limit, null);
            }

            if (sticky)
            {
                return new RegExpExecResult(RegExpOutcome.NoMatch, RegExpLimitKind.None, null);
            }

            // AdvanceStringIndex: by code point with u or v, else by code unit.
            i += _unicode && i + 1 < _n && IsHigh(str[i]) && IsLow(str[i + 1]) ? 2 : 1;
            if (i > _n)
            {
                return new RegExpExecResult(RegExpOutcome.NoMatch, RegExpLimitKind.None, null);
            }
        }
    }

    private bool Refill()
    {
        while (_budget < 0)
        {
            long want = Math.Min(FuelMeter.ChunkSize, Math.Max(1, _fuel.Remaining));
            if (!_fuel.TryConsume(want))
            {
                _limit = RegExpLimitKind.Fuel;
                return false;
            }

            _budget += want;
        }

        return true;
    }

    private void Push(int tag, int a, int b, int c)
    {
        if (_sp + 4 > _stk.Length)
        {
            Grow();
        }

        _stk[_sp] = tag;
        _stk[_sp + 1] = a;
        _stk[_sp + 2] = b;
        _stk[_sp + 3] = c;
        _sp += 4;
    }

    private void Grow()
    {
        if (_stk.Length >= _maxStackInts)
        {
            _limit = RegExpLimitKind.Memory;
            throw new StackLimitException();
        }

        long next = Math.Min(Math.Max(256L, (long)_stk.Length * 2), _maxStackInts);
        Array.Resize(ref _stk, (int)next);
    }

    private void SetReg(int r, int v)
    {
        Push(EntryRestore, r, _regs[r], 0);
        _regs[r] = v;
    }

    // Reads the character at p in the given direction and moves p past it. With u or v a character is a code
    // point (a surrogate pair counts as one); otherwise it is a code unit.
    private bool ReadCp(ref int p, bool back, out int cp)
    {
        if (!back)
        {
            if (p >= _n)
            {
                cp = 0;
                return false;
            }

            cp = _s[p++];
            if (_unicode && IsHigh(cp) && p < _n && IsLow(_s[p]))
            {
                cp = char.ConvertToUtf32((char)cp, _s[p++]);
            }

            return true;
        }

        if (p <= 0)
        {
            cp = 0;
            return false;
        }

        cp = _s[--p];
        if (_unicode && IsLow(cp) && p > 0 && IsHigh(_s[p - 1]))
        {
            cp = char.ConvertToUtf32(_s[--p], (char)cp);
        }

        return true;
    }

    private int Canon(int c) => c <= 0xFFFF ? _canon![c] : (_unicode ? RegExpUnicode.Canonicalize(c, true) : c);

    private bool MatchOne(int kind, int arg, int fl, int c)
    {
        switch (kind)
        {
            case Op.KindChar:
                return ((fl & Op.FlagIgnoreCase) != 0 ? Canon(c) : c) == arg;
            case Op.KindAny:
                return (fl & Op.FlagDotAll) != 0 || !IsLineTerminator(c);
            default:
                return _p.Classes[arg].Matches(c, _canon, _unicode);
        }
    }

    private bool Step(int kind, int arg, int fl, ref int p)
    {
        int np = p;
        if (!ReadCp(ref np, (fl & Op.FlagBack) != 0, out int c) || !MatchOne(kind, arg, fl, c))
        {
            return false;
        }

        p = np;
        return true;
    }

    private bool IsWordAt(CharClass word, int index) => index >= 0 && index < _n && word.Matches(_s[index], null, false);

    // BackreferenceMatcher (22.2.2.7.2); lengths are counted in characters, so with u or v by code points.
    private bool MatchBackref(int rs, int re, ref int pos, bool back, bool ic)
    {
        int g = pos;
        if (back)
        {
            int count = 0;
            int q = rs;
            while (q < re)
            {
                ReadCp(ref q, false, out _);
                count++;
            }

            for (int k = 0; k < count; k++)
            {
                if (!ReadCp(ref g, true, out _))
                {
                    return false;
                }
            }
        }

        int i = rs;
        int j = g;
        while (i < re)
        {
            if (!ReadCp(ref j, false, out int b))
            {
                return false;
            }

            ReadCp(ref i, false, out int a);
            if (a != b && (!ic || Canon(a) != Canon(b)))
            {
                return false;
            }
        }

        pos = back ? g : j;
        return true;
    }

    // Longest string of the trie that matches at pos and has at most 'bound' characters.
    private bool FindString(StringTrie t, int pos, bool back, bool ic, int bound, out int end, out int length)
    {
        int node = 0;
        int p = pos;
        int depth = 0;
        end = 0;
        length = 0;
        while (depth < bound && ReadCp(ref p, back, out int cp))
        {
            node = t.Child(node, ic ? Canon(cp) : cp);
            if (node < 0)
            {
                break;
            }

            depth++;
            if (t.IsTerminal(node))
            {
                end = p;
                length = depth;
            }
        }

        return length > 0;
    }

    // Returns 1 for a match (captures are in _regs), 0 for none from this start, 2 when a limit stopped the run.
    private int Run(int start)
    {
        int[] code = _p.Code;
        int[] regs = _regs;
        string s = _s;
        int n = _n;
        int pc = 0;
        int pos = start;
        _sp = 0;
        while (true)
        {
            if (--_budget < 0 && !Refill())
            {
                return 2;
            }

            bool fail = false;
            switch (code[pc])
            {
                case Op.Char:
                    if (Step(Op.KindChar, code[pc + 1], code[pc + 2], ref pos))
                    {
                        pc += 3;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                case Op.Any:
                    if (Step(Op.KindAny, 0, code[pc + 1], ref pos))
                    {
                        pc += 2;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                case Op.Set:
                    if (Step(Op.KindSet, code[pc + 1], code[pc + 2], ref pos))
                    {
                        pc += 3;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                case Op.Bol:
                    if (pos == 0 || (code[pc + 1] != 0 && IsLineTerminator(s[pos - 1])))
                    {
                        pc += 2;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                case Op.Eol:
                    if (pos == n || (code[pc + 1] != 0 && IsLineTerminator(s[pos])))
                    {
                        pc += 2;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                case Op.WordBoundary:
                {
                    CharClass word = _p.Classes[code[pc + 2]];
                    bool at = IsWordAt(word, pos - 1) != IsWordAt(word, pos);
                    if (at != (code[pc + 1] != 0))
                    {
                        pc += 3;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                }

                case Op.Split:
                    Push(EntryChoice, code[pc + 1], pos, 0);
                    pc += 2;
                    break;
                case Op.Jmp:
                    pc = code[pc + 1];
                    break;
                case Op.Mark:
                    SetReg(code[pc + 1], pos);
                    pc += 2;
                    break;
                case Op.EndGroup:
                {
                    int g = code[pc + 1];
                    int tmp = regs[code[pc + 2]];
                    if (code[pc + 3] == 0)
                    {
                        SetReg(2 * g, tmp);
                        SetReg((2 * g) + 1, pos);
                    }
                    else
                    {
                        SetReg(2 * g, pos);
                        SetReg((2 * g) + 1, tmp);
                    }

                    pc += 4;
                    break;
                }

                case Op.Clear:
                    for (int r = 2 * code[pc + 1]; r <= (2 * code[pc + 2]) + 1; r++)
                    {
                        if (regs[r] != -1)
                        {
                            SetReg(r, -1);
                        }
                    }

                    pc += 3;
                    break;
                case Op.Backref:
                {
                    int fl = code[pc + 2];
                    int rs = -1;
                    int re = -1;
                    foreach (int g in _p.Backrefs[code[pc + 1]])
                    {
                        if (regs[2 * g] >= 0)
                        {
                            rs = regs[2 * g];
                            re = regs[(2 * g) + 1];
                            break;
                        }
                    }

                    if (rs < 0 || MatchBackref(rs, re, ref pos, (fl & Op.FlagBack) != 0, (fl & Op.FlagIgnoreCase) != 0))
                    {
                        pc += 3;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                }

                case Op.LookStart:
                    if (code[pc + 1] == 0)
                    {
                        Push(EntryPosBarrier, 0, pos, 0);
                    }
                    else
                    {
                        Push(EntryNegBarrier, code[pc + 2], pos, 0);
                    }

                    pc += 3;
                    break;
                case Op.LookEnd:
                    if (code[pc + 1] == 0)
                    {
                        // Positive lookaround succeeded: it is atomic, so drop its choice points but keep the
                        // register undo entries, then continue from where it started.
                        int b = _sp - 4;
                        while (_stk[b] != EntryPosBarrier)
                        {
                            b -= 4;
                        }

                        pos = _stk[b + 2];
                        int w = b;
                        for (int k = b + 4; k < _sp; k += 4)
                        {
                            if (_stk[k] == EntryRestore)
                            {
                                _stk[w] = EntryRestore;
                                _stk[w + 1] = _stk[k + 1];
                                _stk[w + 2] = _stk[k + 2];
                                w += 4;
                            }
                        }

                        _sp = w;
                        pc += 2;
                    }
                    else
                    {
                        // Negative lookaround whose body matched: undo its register changes and fail.
                        while (true)
                        {
                            _sp -= 4;
                            int tag = _stk[_sp];
                            if (tag == EntryRestore)
                            {
                                regs[_stk[_sp + 1]] = _stk[_sp + 2];
                            }
                            else if (tag == EntryNegBarrier)
                            {
                                break;
                            }
                        }

                        fail = true;
                    }

                    break;
                case Op.SetReg:
                    SetReg(code[pc + 1], code[pc + 2]);
                    pc += 3;
                    break;
                case Op.LoopSplit:
                {
                    int c = regs[code[pc + 1]];
                    if (c < code[pc + 2])
                    {
                        pc += 6;
                    }
                    else if (c >= code[pc + 3])
                    {
                        pc = code[pc + 5];
                    }
                    else if (code[pc + 4] != 0)
                    {
                        Push(EntryChoice, code[pc + 5], pos, 0);
                        pc += 6;
                    }
                    else
                    {
                        Push(EntryChoice, pc + 6, pos, 0);
                        pc = code[pc + 5];
                    }

                    break;
                }

                case Op.LoopEnd:
                {
                    int c = regs[code[pc + 1]];
                    int posReg = code[pc + 2];

                    // RepeatMatcher step 2.b: once the minimum is met, an iteration that matched the empty string fails.
                    if (posReg >= 0 && c >= code[pc + 3] && pos == regs[posReg])
                    {
                        fail = true;
                    }
                    else
                    {
                        SetReg(code[pc + 1], c == int.MaxValue ? c : c + 1);
                        pc = code[pc + 4];
                    }

                    break;
                }

                case Op.EmptyCheck:
                    if (pos == regs[code[pc + 1]])
                    {
                        fail = true;
                    }
                    else
                    {
                        pc += 2;
                    }

                    break;
                case Op.Repeat:
                {
                    int kind = code[pc + 1];
                    int arg = code[pc + 2];
                    int fl = code[pc + 3];
                    int min = code[pc + 4];
                    int max = code[pc + 5];
                    bool back = (fl & Op.FlagBack) != 0;
                    int count = 0;
                    int p = pos;
                    int minPos = pos;
                    if (code[pc + 6] != 0)
                    {
                        while (count < max && Step(kind, arg, fl, ref p))
                        {
                            count++;
                            if (count == min)
                            {
                                minPos = p;
                            }
                        }

                        _budget -= count;
                        if (count < min)
                        {
                            fail = true;
                        }
                        else
                        {
                            if (count > min)
                            {
                                Push(back ? EntryRepeatBack : EntryRepeatFwd, pc + 7, minPos, p);
                            }

                            pos = p;
                            pc += 7;
                        }
                    }
                    else
                    {
                        while (count < min && Step(kind, arg, fl, ref p))
                        {
                            count++;
                        }

                        _budget -= count;
                        if (count < min)
                        {
                            fail = true;
                        }
                        else
                        {
                            if (max > min)
                            {
                                Push(EntryLazy, pc, p, count);
                            }

                            pos = p;
                            pc += 7;
                        }
                    }

                    if (_budget < 0 && !Refill())
                    {
                        return 2;
                    }

                    break;
                }

                case Op.Strings:
                {
                    int fl = code[pc + 2];
                    if (FindString(_p.Tries[code[pc + 1]], pos, (fl & Op.FlagBack) != 0, (fl & Op.FlagIgnoreCase) != 0, int.MaxValue, out int end, out int len))
                    {
                        if (len > 2)
                        {
                            Push(EntryStrings, pc, pos, len - 1);
                        }

                        pos = end;
                        pc += 3;
                    }
                    else
                    {
                        fail = true;
                    }

                    break;
                }

                case Op.Match:
                    regs[0] = start;
                    regs[1] = pos;
                    return 1;
                default:
                    throw new InvalidOperationException("Corrupt RegExp program.");
            }

            if (!fail)
            {
                continue;
            }

            // Backtrack: pop entries until one offers another way forward.
            while (true)
            {
                if (_sp == 0)
                {
                    return 0;
                }

                if (--_budget < 0 && !Refill())
                {
                    return 2;
                }

                _sp -= 4;
                int[] stk = _stk;
                int sp = _sp;
                int tag = stk[sp];
                if (tag == EntryRestore)
                {
                    regs[stk[sp + 1]] = stk[sp + 2];
                }
                else if (tag == EntryChoice || tag == EntryNegBarrier)
                {
                    pc = stk[sp + 1];
                    pos = stk[sp + 2];
                    break;
                }
                else if (tag == EntryRepeatFwd)
                {
                    // Give back one character of a greedy loop.
                    int minPos = stk[sp + 2];
                    int cur = stk[sp + 3] - 1;
                    if (_unicode && cur > minPos && IsLow(s[cur]) && IsHigh(s[cur - 1]))
                    {
                        cur--;
                    }

                    if (cur > minPos)
                    {
                        stk[sp + 3] = cur;
                        _sp += 4;
                    }

                    pc = stk[sp + 1];
                    pos = cur;
                    break;
                }
                else if (tag == EntryRepeatBack)
                {
                    int maxPos = stk[sp + 2];
                    int cur = stk[sp + 3] + 1;
                    if (_unicode && cur < maxPos && IsHigh(s[cur - 1]) && IsLow(s[cur]))
                    {
                        cur++;
                    }

                    if (cur < maxPos)
                    {
                        stk[sp + 3] = cur;
                        _sp += 4;
                    }

                    pc = stk[sp + 1];
                    pos = cur;
                    break;
                }
                else if (tag == EntryLazy)
                {
                    // Take one more iteration of a lazy loop.
                    int rpc = stk[sp + 1];
                    int p = stk[sp + 2];
                    int count = stk[sp + 3];
                    if (Step(code[rpc + 1], code[rpc + 2], code[rpc + 3], ref p))
                    {
                        count++;
                        _budget--;
                        if (count < code[rpc + 5])
                        {
                            stk[sp + 2] = p;
                            stk[sp + 3] = count;
                            _sp += 4;
                        }

                        pc = rpc + 7;
                        pos = p;
                        break;
                    }
                }
                else if (tag == EntryStrings)
                {
                    int spc = stk[sp + 1];
                    int fl = code[spc + 2];
                    int bound = stk[sp + 3];
                    if (FindString(_p.Tries[code[spc + 1]], stk[sp + 2], (fl & Op.FlagBack) != 0, (fl & Op.FlagIgnoreCase) != 0, bound, out int end, out int len))
                    {
                        if (len > 2)
                        {
                            stk[sp + 3] = len - 1;
                            _sp += 4;
                        }

                        pc = spc + 3;
                        pos = end;
                        break;
                    }
                }

                // EntryPosBarrier and exhausted entries just drop.
            }
        }
    }
}
