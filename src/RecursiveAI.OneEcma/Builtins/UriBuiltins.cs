using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>encodeURI, encodeURIComponent, decodeURI, decodeURIComponent (ECMA-262 19.2.6) and escape, unescape (B.2.1).</summary>
internal static partial class Builtins
{
    private const string UriReserved = ";/?:@&=+$,";
    private const string UriMark = "-_.!~*'()";
    private const string HexUpper = "0123456789ABCDEF";

    private static JsException UriMalformed() => Errors.Make("URIError", "URI malformed");

    private static bool IsAsciiAlnum(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static void SetupUriFunctions(Realm r)
    {
        JsObject g = r.Global;
        r.Method(g, "decodeURI", 1, (t, a, nt) => Ops.Str(Decode(Ops.ToStr(A(a, 0)), UriReserved + "#")));
        r.Method(g, "decodeURIComponent", 1, (t, a, nt) => Ops.Str(Decode(Ops.ToStr(A(a, 0)), string.Empty)));
        r.Method(g, "encodeURI", 1, (t, a, nt) => Ops.Str(Encode(Ops.ToStr(A(a, 0)), UriReserved + UriMark + "#")));
        r.Method(g, "encodeURIComponent", 1, (t, a, nt) => Ops.Str(Encode(Ops.ToStr(A(a, 0)), UriMark)));
        r.Method(g, "escape", 1, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            Cost(s.Length);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (IsAsciiAlnum(c) || c is '@' or '*' or '_' or '+' or '-' or '.' or '/')
                {
                    sb.Append(c);
                }
                else if (c < 256)
                {
                    sb.Append('%').Append(HexUpper[c >> 4]).Append(HexUpper[c & 15]);
                }
                else
                {
                    sb.Append("%u").Append(HexUpper[c >> 12]).Append(HexUpper[(c >> 8) & 15]).Append(HexUpper[(c >> 4) & 15]).Append(HexUpper[c & 15]);
                }

                if (sb.Length > Agent.Current.Limits.MaxStringLength)
                {
                    throw Errors.Range("Invalid string length");
                }
            }

            return Ops.Str(sb.ToString());
        });
        r.Method(g, "unescape", 1, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            Cost(s.Length);
            var sb = new StringBuilder(s.Length);
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '%')
                {
                    if (k + 6 <= s.Length && s[k + 1] == 'u' && Hex4(s, k + 2, out int u))
                    {
                        sb.Append((char)u);
                        k += 5;
                        continue;
                    }

                    if (k + 3 <= s.Length && Hex2(s, k + 1, out int b))
                    {
                        sb.Append((char)b);
                        k += 2;
                        continue;
                    }
                }

                sb.Append(c);
            }

            return Ops.Str(sb.ToString());
        });
    }

    private static bool Hex2(string s, int i, out int v)
    {
        int h = NumberConv.DigitValue(s[i]);
        int l = NumberConv.DigitValue(s[i + 1]);
        v = (h << 4) | l;
        return h is >= 0 and < 16 && l is >= 0 and < 16;
    }

    private static bool Hex4(string s, int i, out int v)
    {
        v = 0;
        for (int k = 0; k < 4; k++)
        {
            int d = NumberConv.DigitValue(s[i + k]);
            if (d is < 0 or >= 16)
            {
                return false;
            }

            v = (v << 4) | d;
        }

        return true;
    }

    /// <summary>Encode (19.2.6.5): <paramref name="unescapedExtra"/> lists the non-alphanumeric characters kept as they are.</summary>
    private static string Encode(string s, string unescapedExtra)
    {
        Cost(s.Length);
        var sb = new StringBuilder(s.Length);
        Span<byte> buf = stackalloc byte[4];
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (IsAsciiAlnum(c) || unescapedExtra.Contains(c, StringComparison.Ordinal))
            {
                sb.Append(c);
                continue;
            }

            int cp = c;
            if (char.IsLowSurrogate(c))
            {
                throw UriMalformed();
            }

            if (char.IsHighSurrogate(c))
            {
                if (k + 1 >= s.Length || !char.IsLowSurrogate(s[k + 1]))
                {
                    throw UriMalformed();
                }

                cp = char.ConvertToUtf32(c, s[++k]);
            }

            int n = Utf8Encode(cp, buf);
            for (int i = 0; i < n; i++)
            {
                sb.Append('%').Append(HexUpper[buf[i] >> 4]).Append(HexUpper[buf[i] & 15]);
            }

            if (sb.Length > Agent.Current.Limits.MaxStringLength)
            {
                throw Errors.Range("Invalid string length");
            }
        }

        return sb.ToString();
    }

    private static int Utf8Encode(int cp, Span<byte> buf)
    {
        if (cp < 0x80)
        {
            buf[0] = (byte)cp;
            return 1;
        }

        if (cp < 0x800)
        {
            buf[0] = (byte)(0xC0 | (cp >> 6));
            buf[1] = (byte)(0x80 | (cp & 0x3F));
            return 2;
        }

        if (cp < 0x10000)
        {
            buf[0] = (byte)(0xE0 | (cp >> 12));
            buf[1] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            buf[2] = (byte)(0x80 | (cp & 0x3F));
            return 3;
        }

        buf[0] = (byte)(0xF0 | (cp >> 18));
        buf[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
        buf[2] = (byte)(0x80 | ((cp >> 6) & 0x3F));
        buf[3] = (byte)(0x80 | (cp & 0x3F));
        return 4;
    }

    /// <summary>Decode (19.2.6.6).</summary>
    private static string Decode(string s, string reservedSet)
    {
        Cost(s.Length);
        int first = s.IndexOf('%', StringComparison.Ordinal);
        if (first < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, first);
        for (int k = first; k < s.Length; k++)
        {
            char c = s[k];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }

            int start = k;
            if (k + 2 >= s.Length || !Hex2(s, k + 1, out int b))
            {
                throw UriMalformed();
            }

            k += 2;
            if ((b & 0x80) == 0)
            {
                char ch = (char)b;
                if (reservedSet.Contains(ch, StringComparison.Ordinal))
                {
                    sb.Append(s, start, k - start + 1);
                }
                else
                {
                    sb.Append(ch);
                }

                continue;
            }

            int n = b >= 0xF8 ? 5 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            if (n == 1 || n > 4)
            {
                throw UriMalformed();
            }

            int cp = b & (0xFF >> (n + 1));
            if (k + (3 * (n - 1)) >= s.Length)
            {
                throw UriMalformed();
            }

            for (int j = 1; j < n; j++)
            {
                k++;
                if (s[k] != '%' || !Hex2(s, k + 1, out int cont) || (cont & 0xC0) != 0x80)
                {
                    throw UriMalformed();
                }

                k += 2;
                cp = (cp << 6) | (cont & 0x3F);
            }

            // Reject overlong forms, surrogates and values past U+10FFFF.
            int min = n == 2 ? 0x80 : n == 3 ? 0x800 : 0x10000;
            if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            {
                throw UriMalformed();
            }

            if (cp < 0x10000)
            {
                sb.Append((char)cp);
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }
}
