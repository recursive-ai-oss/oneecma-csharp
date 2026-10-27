using System.Text;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>
/// Applies UTS #35 collation rule text (the <c>&amp;</c> reset and <c>&lt;</c>, <c>&lt;&lt;</c>,
/// <c>&lt;&lt;&lt;</c>, <c>=</c> relations, <c>;</c> and <c>,</c> shorthands, quoting and escapes, the
/// <c>[last primary ignorable]</c> position, <c>[import]</c>) to a <see cref="CollationTable"/>.
/// Settings that do not change the order of Latin text (<c>[normalization]</c>, <c>[suppressContractions]</c>)
/// are read and ignored; syntax the supported locales do not use (<c>[before]</c>, <c>/</c>, <c>|</c>,
/// <c>&lt;*</c> lists, <c>&lt;&lt;&lt;&lt;</c>, <c>[reorder]</c>) throws <see cref="NotSupportedException"/>
/// rather than collating wrongly.
/// </summary>
internal sealed class CollationRules
{
    private readonly string _text;
    private readonly CollationTable _table;
    private readonly Func<string, string?> _import;
    private int _pos;
    private Ce[] _reference = [];

    private CollationRules(string text, CollationTable table, Func<string, string?> import)
    {
        _text = text;
        _table = table;
        _import = import;
    }

    /// <summary>Applies rule text to a table.</summary>
    /// <param name="rules">UTS #35 rule syntax.</param>
    /// <param name="table">The table to extend.</param>
    /// <param name="import">Resolves the argument of <c>[import x]</c> (a locale with <c>-u-co-</c> type) to rule text, or null.</param>
    /// <exception cref="FormatException">The rules are malformed.</exception>
    /// <exception cref="NotSupportedException">The rules use syntax this collator does not implement.</exception>
    internal static void Apply(string rules, CollationTable table, Func<string, string?> import) =>
        new CollationRules(rules, table, import).Run();

    private void Run()
    {
        while (true)
        {
            SkipSpace();
            if (_pos >= _text.Length)
            {
                return;
            }

            char c = _text[_pos];
            switch (c)
            {
                case '&':
                    _pos++;
                    Reset();
                    break;
                case '[':
                    Setting();
                    break;
                case '<' or '=' or ';' or ',':
                    Relation();
                    break;
                default:
                    throw Bad($"unexpected '{c}'");
            }
        }
    }

    private FormatException Bad(string what) => new($"Collation rules: {what} at offset {_pos}.");

    private void SkipSpace()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '#')
            {
                while (_pos < _text.Length && _text[_pos] != '\n')
                {
                    _pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private void Reset()
    {
        SkipSpace();
        if (_pos < _text.Length && _text[_pos] == '[')
        {
            string setting = ReadBracket();
            if (setting.StartsWith("[last primary ignorable", StringComparison.Ordinal))
            {
                // Level-1 ignorables sort after one another by secondary weight; a new element goes past the last.
                _reference = [new Ce(0, (uint)_table.Root.LastPrimaryIgnorableSecondary << Ce.WeightScale, Ce.CommonTertiary, Ce.CommonTertiary, 0)];
                return;
            }

            throw new NotSupportedException($"Collation rules: reset position {setting} is not supported.");
        }

        string key = ReadString();
        if (key.Length == 0)
        {
            throw Bad("empty reset");
        }

        _reference = _table.ElementsOf(key);
        if (_reference.Length == 0)
        {
            throw Bad($"reset to a completely ignorable string");
        }
    }

    private void Setting()
    {
        string setting = ReadBracket();
        string body = setting[1..^1].Trim();
        int space = body.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? body : body[..space];
        switch (name)
        {
            case "import":
                string target = body[(space + 1)..].Trim();
                string? text = _import(target) ?? throw new FormatException($"Collation rules: cannot import '{target}'.");
                Apply(text, _table, _import);
                _reference = [];
                break;
            case "suppressContractions":
                SuppressContractions(body[(space + 1)..]);
                break;
            case "normalization" or "suppress" or "optimize" or "strength" or "backwards" or "alternate"
                or "caseFirst" or "caseLevel" or "numeric" or "hiraganaQ" or "maxVariable":
                // Order of Latin text does not depend on these as they appear in the supported locales' rules.
                break;
            default:
                throw new NotSupportedException($"Collation rules: setting [{name}] is not supported.");
        }
    }

    // [suppressContractions [set]]: a UnicodeSet of single characters and ranges (with \uXXXX escapes).
    private void SuppressContractions(string set)
    {
        string body = set.Trim().TrimStart('[').TrimEnd(']');
        var cps = new List<int>();
        for (int i = 0; i < body.Length;)
        {
            if (char.IsWhiteSpace(body[i]))
            {
                i++;
                continue;
            }

            int from = SetChar(body, ref i);
            int to = from;
            if (i < body.Length - 1 && body[i] == '-')
            {
                i++;
                to = SetChar(body, ref i);
            }

            for (int cp = from; cp <= to; cp++)
            {
                cps.Add(cp);
            }
        }

        foreach (int cp in cps)
        {
            _table.SuppressRootContractions(cp);
        }
    }

    private static int SetChar(string s, ref int i)
    {
        if (s[i] == '\\' && i + 5 < s.Length + 1 && s[i + 1] == 'u')
        {
            int cp = Convert.ToInt32(s.Substring(i + 2, 4), 16);
            i += 6;
            return cp;
        }

        int c = char.ConvertToUtf32(s, i);
        i += char.IsSurrogatePair(s, i) ? 2 : 1;
        return c;
    }

    // Returns the text of a bracketed setting including nested brackets, and moves past it.
    private string ReadBracket()
    {
        int start = _pos;
        int depth = 0;
        while (_pos < _text.Length)
        {
            char c = _text[_pos++];
            if (c == '\\' && _pos < _text.Length)
            {
                _pos++;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && --depth == 0)
            {
                return _text[start.._pos];
            }
        }

        throw Bad("unterminated '['");
    }

    private void Relation()
    {
        char c = _text[_pos];
        int level;
        if (c == '=')
        {
            _pos++;
            level = 0;
        }
        else if (c == ';')
        {
            _pos++;
            level = 2;
        }
        else if (c == ',')
        {
            _pos++;
            level = 3;
        }
        else
        {
            level = 0;
            while (_pos < _text.Length && _text[_pos] == '<')
            {
                level++;
                _pos++;
            }

            if (level == 4)
            {
                throw new NotSupportedException("Collation rules: quaternary relations (<<<<) are not supported.");
            }

            if (_pos < _text.Length && _text[_pos] == '*')
            {
                throw new NotSupportedException("Collation rules: list relations (<*) are not supported.");
            }
        }

        if (_reference.Length == 0)
        {
            throw Bad("relation without a reset");
        }

        SkipSpace();
        string target = ReadString();
        if (target.Length == 0)
        {
            throw Bad("missing string after relation");
        }

        if (_pos < _text.Length && _text[_pos] is '/' or '|')
        {
            throw new NotSupportedException("Collation rules: expansions (/) and prefixes (|) are not supported.");
        }

        Ce[] elements = (Ce[])_reference.Clone();
        ref Ce last = ref elements[^1];
        switch (level)
        {
            case 0:
                break;
            case 1:
                // A new primary directly above the reference; its lower levels start at the common weights.
                last = new Ce(last.Primary + 1, Ce.CommonSecondary, Ce.CommonTertiary, Ce.CommonTertiary, (byte)(last.Flags & Ce.VariableFlag));
                break;
            case 2:
                last = new Ce(last.Primary, last.Secondary + 1, Ce.CommonTertiary, Ce.CommonTertiary, (byte)(last.Flags & Ce.VariableFlag));
                break;
            default:
                last = new Ce(last.Primary, last.Secondary, last.Tertiary + 1, last.PlainTertiary + 1, (byte)(last.Flags & Ce.VariableFlag));
                break;
        }

        // Case of each tailored element comes from the letters it is written with: "Ä" is upper then lower
        // like "Ae", and a contraction written with both cases ("Ch") is mixed, ordering between "ch" and "CH".
        if (level != 0)
        {
            Ce[] written = _table.ElementsOf(target);
            int cased = written.Count(w => w.Primary != 0);
            int upperCount = written.Count(w => w.Primary != 0 && w.IsUpper);
            for (int i = 0; i < elements.Length; i++)
            {
                byte flags = (byte)(elements[i].Flags & Ce.VariableFlag);
                if (elements.Length == 1 && cased > 1)
                {
                    flags |= upperCount == cased ? Ce.UpperFlag : upperCount == 0 ? (byte)0 : Ce.MixedFlag;
                }
                else if (i < written.Length && written[i].Primary != 0 && written[i].IsUpper)
                {
                    flags |= Ce.UpperFlag;
                }

                elements[i] = elements[i] with { Flags = flags };
            }
        }

        using var cps = new CodePointBuffer(target.Length + 2);
        Normalizer.Decompose(target, false, cps);
        _table.Define(cps.Span, elements);
        _reference = elements;
    }

    // A string runs until whitespace or a syntax character. Quotes ('...'), '' and backslash escapes make
    // syntax characters literal.
    private string ReadString()
    {
        var sb = new StringBuilder();
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (c == '\'')
            {
                _pos++;
                if (_pos < _text.Length && _text[_pos] == '\'')
                {
                    sb.Append('\'');
                    _pos++;
                    continue;
                }

                while (_pos < _text.Length && _text[_pos] != '\'')
                {
                    sb.Append(_text[_pos++]);
                }

                if (_pos >= _text.Length)
                {
                    throw Bad("unterminated quote");
                }

                _pos++;
            }
            else if (c == '\\')
            {
                _pos++;
                if (_pos >= _text.Length)
                {
                    throw Bad("dangling backslash");
                }

                char e = _text[_pos++];
                int digits = e == 'u' ? 4 : e == 'U' ? 8 : 0;
                if (digits > 0)
                {
                    if (_pos + digits > _text.Length)
                    {
                        throw Bad("short \\u escape");
                    }

                    int cp = Convert.ToInt32(_text.Substring(_pos, digits), 16);
                    _pos += digits;
                    sb.Append(char.ConvertFromUtf32(cp));
                }
                else
                {
                    sb.Append(e);
                }
            }
            else if (char.IsWhiteSpace(c) || c is '<' or '=' or ';' or ',' or '&' or '[' or ']' or '/' or '|' or '#')
            {
                break;
            }
            else
            {
                sb.Append(c);
                _pos++;
            }
        }

        return sb.ToString();
    }
}
