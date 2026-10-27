using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Uint8Array base64 and hex conversion (ECMA-262 23.3).</summary>
internal static partial class TypedArrays
{
    private const string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string Base64UrlChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private readonly record struct Decoded(int Read, List<byte> Bytes, string? Error);

    private static JsTypedArray ValidateUint8(JsValue t, string method)
    {
        JsTypedArray ta = Require(t);
        if (ta.Kind != ElementKind.Uint8)
        {
            throw Errors.Type("Method Uint8Array.prototype." + method + " called on incompatible receiver " + Ops.DisplayValue(t));
        }

        return ta;
    }

    /// <summary>GetUint8ArrayBytes (23.3.3.2).</summary>
    private static ReadOnlySpan<byte> Uint8Bytes(JsTypedArray ta, string method)
    {
        if (ta.IsOutOfBounds)
        {
            throw DetachedOp("Uint8Array.prototype." + method);
        }

        return ta.Buffer.Data.AsSpan(ta.ByteOffset, ta.Length);
    }

    private static string AlphabetOption(JsObject opts)
    {
        JsValue alphabet = opts.Get("alphabet");
        if (alphabet.IsUndefined)
        {
            return "base64";
        }

        if (alphabet.IsString && alphabet.AsString is "base64" or "base64url")
        {
            return alphabet.AsString;
        }

        throw Errors.Type("Invalid alphabet: expected \"base64\" or \"base64url\"");
    }

    private static string LastChunkOption(JsObject opts)
    {
        JsValue v = opts.Get("lastChunkHandling");
        if (v.IsUndefined)
        {
            return "loose";
        }

        if (v.IsString && v.AsString is "loose" or "strict" or "stop-before-partial")
        {
            return v.AsString;
        }

        throw Errors.Type("Invalid lastChunkHandling: expected \"loose\", \"strict\" or \"stop-before-partial\"");
    }

    /// <summary>GetOptionsObject (7.3.36).</summary>
    private static JsObject OptionsObject(JsValue options, Realm r)
    {
        if (options.IsUndefined)
        {
            return new JsObject((JsObject?)null);
        }

        return options.Ref as JsObject ?? throw Errors.Type("The \"options\" argument must be of type object");
    }

    private static int SkipWhitespace(string s, int index)
    {
        while (index < s.Length && s[index] is '\t' or '\n' or '\f' or '\r' or ' ')
        {
            index++;
        }

        return index;
    }

    private static int Base64Value(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    private static void DecodeFull(List<byte> bytes, int[] c)
    {
        int n = (c[0] << 18) | (c[1] << 12) | (c[2] << 6) | c[3];
        bytes.Add((byte)(n >> 16));
        bytes.Add((byte)(n >> 8));
        bytes.Add((byte)n);
    }

    /// <summary>DecodeFinalBase64Chunk (23.3.3.5); false when throwOnExtraBits rejects the chunk.</summary>
    private static bool DecodeFinal(List<byte> bytes, int[] c, int chunkLength, bool throwOnExtraBits)
    {
        var padded = new int[4];
        for (int i = 0; i < 4; i++)
        {
            padded[i] = i < chunkLength ? c[i] : 0;
        }

        int n = (padded[0] << 18) | (padded[1] << 12) | (padded[2] << 6) | padded[3];
        byte b0 = (byte)(n >> 16), b1 = (byte)(n >> 8), b2 = (byte)n;
        if (chunkLength == 2)
        {
            if (throwOnExtraBits && b1 != 0)
            {
                return false;
            }

            bytes.Add(b0);
            return true;
        }

        if (throwOnExtraBits && b2 != 0)
        {
            return false;
        }

        bytes.Add(b0);
        bytes.Add(b1);
        return true;
    }

    /// <summary>FromBase64 (23.3.3.7).</summary>
    private static Decoded FromBase64(string s, string alphabet, string lastChunkHandling, long maxLength)
    {
        Agent.Current.Alloc(s.Length);
        var bytes = new List<byte>();
        if (maxLength == 0)
        {
            return new Decoded(0, bytes, null);
        }

        const string Bad = "The input is not valid base64";
        int read = 0;
        var chunk = new int[4];
        int chunkLength = 0;
        int index = 0;
        int length = s.Length;
        bool url = alphabet == "base64url";
        Agent.Current.Charge((length >> 3) + 1);
        while (true)
        {
            index = SkipWhitespace(s, index);
            if (index == length)
            {
                if (chunkLength > 0)
                {
                    if (lastChunkHandling == "stop-before-partial")
                    {
                        return new Decoded(read, bytes, null);
                    }

                    if (lastChunkHandling == "strict")
                    {
                        return new Decoded(read, bytes, "The base64 input ends with an incomplete chunk");
                    }

                    if (chunkLength == 1)
                    {
                        return new Decoded(read, bytes, "The base64 input ends with an incomplete chunk");
                    }

                    DecodeFinal(bytes, chunk, chunkLength, false);
                }

                return new Decoded(length, bytes, null);
            }

            char ch = s[index];
            index++;
            if (ch == '=')
            {
                if (chunkLength < 2)
                {
                    return new Decoded(read, bytes, Bad);
                }

                index = SkipWhitespace(s, index);
                if (chunkLength == 2)
                {
                    if (index == length)
                    {
                        return lastChunkHandling == "stop-before-partial"
                            ? new Decoded(read, bytes, null)
                            : new Decoded(read, bytes, "The base64 input ends with an incomplete chunk");
                    }

                    if (s[index] == '=')
                    {
                        index = SkipWhitespace(s, index + 1);
                    }
                }

                if (index < length)
                {
                    return new Decoded(read, bytes, Bad);
                }

                if (!DecodeFinal(bytes, chunk, chunkLength, lastChunkHandling == "strict"))
                {
                    return new Decoded(read, bytes, "The base64 input has non-zero padding bits");
                }

                return new Decoded(length, bytes, null);
            }

            if (url)
            {
                if (ch is '+' or '/')
                {
                    return new Decoded(read, bytes, Bad);
                }

                if (ch == '-')
                {
                    ch = '+';
                }
                else if (ch == '_')
                {
                    ch = '/';
                }
            }

            int v = Base64Value(ch);
            if (v < 0)
            {
                return new Decoded(read, bytes, Bad);
            }

            long remaining = maxLength - bytes.Count;
            if ((remaining == 1 && chunkLength == 2) || (remaining == 2 && chunkLength == 3))
            {
                return new Decoded(read, bytes, null);
            }

            chunk[chunkLength++] = v;
            if (chunkLength == 4)
            {
                DecodeFull(bytes, chunk);
                chunkLength = 0;
                read = index;
                if (bytes.Count == maxLength)
                {
                    return new Decoded(read, bytes, null);
                }
            }
        }
    }

    /// <summary>FromHex (23.3.3.8).</summary>
    private static Decoded FromHex(string s, long maxLength)
    {
        Agent.Current.Alloc(s.Length / 2);
        var bytes = new List<byte>();
        if (s.Length % 2 != 0)
        {
            return new Decoded(0, bytes, "The hex input has an odd length");
        }

        Agent.Current.Charge((s.Length >> 3) + 1);
        int read = 0;
        while (read < s.Length && bytes.Count < maxLength)
        {
            int hi = HexValue(s[read]);
            int lo = HexValue(s[read + 1]);
            if (hi < 0 || lo < 0)
            {
                return new Decoded(read, bytes, "The hex input contains a character that is not a hexadecimal digit");
            }

            read += 2;
            bytes.Add((byte)((hi << 4) | lo));
        }

        return new Decoded(read, bytes, null);
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static JsValue FromDecoded(Realm r, Decoded d)
    {
        if (d.Error is not null)
        {
            throw Errors.Syntax(d.Error);
        }

        JsTypedArray ta = AllocateLength(ProtoFor(r, ElementKind.Uint8), ElementKind.Uint8, d.Bytes.Count);
        d.Bytes.CopyTo(ta.Buffer.Data);
        return JsValue.FromObject(ta);
    }

    private static JsValue ResultRecord(Realm r, Decoded d, JsTypedArray into, string method)
    {
        // The bytes decoded before an error are still written (SetUint8ArrayBytes precedes the throw).
        d.Bytes.CopyTo(into.Buffer.Data, into.ByteOffset);
        if (d.Error is not null)
        {
            throw Errors.Syntax(d.Error);
        }

        JsObject res = r.NewObject();
        res.CreateDataPropertyOrThrow("read", Num(d.Read));
        res.CreateDataPropertyOrThrow("written", Num(d.Bytes.Count));
        return JsValue.FromObject(res);
    }

    /// <summary>Refuses a result string longer than the engine's string limit and charges its memory before it is built.</summary>
    private static void ChargeText(long chars)
    {
        if (chars > Agent.Current.Limits.MaxStringLength)
        {
            throw Errors.Range("Invalid string length");
        }

        Agent.Current.Alloc(chars * 2);
    }

    private static string ToBase64(ReadOnlySpan<byte> data, bool url, bool omitPadding)
    {
        string table = url ? Base64UrlChars : Base64Chars;
        ChargeText(((long)data.Length + 2) / 3 * 4);
        var sb = new StringBuilder((data.Length + 2) / 3 * 4);
        int i = 0;
        for (; i + 2 < data.Length; i += 3)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            sb.Append(table[n >> 18]).Append(table[(n >> 12) & 63]).Append(table[(n >> 6) & 63]).Append(table[n & 63]);
        }

        int rest = data.Length - i;
        if (rest == 1)
        {
            int n = data[i] << 16;
            sb.Append(table[n >> 18]).Append(table[(n >> 12) & 63]);
            if (!omitPadding)
            {
                sb.Append("==");
            }
        }
        else if (rest == 2)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8);
            sb.Append(table[n >> 18]).Append(table[(n >> 12) & 63]).Append(table[(n >> 6) & 63]);
            if (!omitPadding)
            {
                sb.Append('=');
            }
        }

        return sb.ToString();
    }

    /// <summary>Adds the Uint8Array-only members; called after the concrete constructors are created.</summary>
    internal static void SetupUint8Extras(Realm r)
    {
        JsObject ctor = r.Intrinsics["%Uint8Array%"];
        JsObject proto = r.Intrinsics["%Uint8Array.prototype%"];

        // 23.3.1.1 Uint8Array.fromBase64 ( string [ , options ] )
        r.Method(ctor, "fromBase64", 1, (t, a, nt) =>
        {
            if (!A(a, 0).IsString)
            {
                throw Errors.Type("Uint8Array.fromBase64: the first argument must be a string");
            }

            JsObject opts = OptionsObject(A(a, 1), r);
            string alphabet = AlphabetOption(opts);
            string last = LastChunkOption(opts);
            return FromDecoded(r, FromBase64(A(a, 0).AsString, alphabet, last, 9007199254740991L));
        });

        // 23.3.1.2 Uint8Array.fromHex ( string )
        r.Method(ctor, "fromHex", 1, (t, a, nt) =>
        {
            if (!A(a, 0).IsString)
            {
                throw Errors.Type("Uint8Array.fromHex: the first argument must be a string");
            }

            return FromDecoded(r, FromHex(A(a, 0).AsString, 9007199254740991L));
        });

        // 23.3.2.1 Uint8Array.prototype.setFromBase64 ( string [ , options ] )
        r.Method(proto, "setFromBase64", 1, (t, a, nt) =>
        {
            JsTypedArray into = ValidateUint8(t, "setFromBase64");
            if (!A(a, 0).IsString)
            {
                throw Errors.Type("Uint8Array.prototype.setFromBase64: the first argument must be a string");
            }

            JsObject opts = OptionsObject(A(a, 1), r);
            string alphabet = AlphabetOption(opts);
            string last = LastChunkOption(opts);
            if (into.IsOutOfBounds)
            {
                throw DetachedOp("Uint8Array.prototype.setFromBase64");
            }

            return ResultRecord(r, FromBase64(A(a, 0).AsString, alphabet, last, into.Length), into, "setFromBase64");
        });

        // 23.3.2.2 Uint8Array.prototype.setFromHex ( string )
        r.Method(proto, "setFromHex", 1, (t, a, nt) =>
        {
            JsTypedArray into = ValidateUint8(t, "setFromHex");
            if (!A(a, 0).IsString)
            {
                throw Errors.Type("Uint8Array.prototype.setFromHex: the first argument must be a string");
            }

            if (into.IsOutOfBounds)
            {
                throw DetachedOp("Uint8Array.prototype.setFromHex");
            }

            return ResultRecord(r, FromHex(A(a, 0).AsString, into.Length), into, "setFromHex");
        });

        // 23.3.2.3 Uint8Array.prototype.toBase64 ( [ options ] )
        r.Method(proto, "toBase64", 0, (t, a, nt) =>
        {
            JsTypedArray o = ValidateUint8(t, "toBase64");
            JsObject opts = OptionsObject(A(a, 0), r);
            string alphabet = AlphabetOption(opts);
            bool omit = Ops.ToBoolean(opts.Get("omitPadding"));
            ReadOnlySpan<byte> data = Uint8Bytes(o, "toBase64");
            Work(data.Length);
            return Ops.Str(ToBase64(data, alphabet == "base64url", omit));
        });

        // 23.3.2.4 Uint8Array.prototype.toHex ( )
        r.Method(proto, "toHex", 0, (t, a, nt) =>
        {
            JsTypedArray o = ValidateUint8(t, "toHex");
            ReadOnlySpan<byte> data = Uint8Bytes(o, "toHex");
            Work(data.Length);
            const string Digits = "0123456789abcdef";
            ChargeText((long)data.Length * 2);
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
            {
                sb.Append(Digits[b >> 4]).Append(Digits[b & 15]);
            }

            return Ops.Str(sb.ToString());
        });
    }
}
