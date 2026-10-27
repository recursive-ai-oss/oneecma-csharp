using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>
/// The record of everything a run took from outside the engine, in order: the clock, random numbers, time-zone answers, modules,
/// host function results, the settling of host tasks, weak-reference collections, and the entry calls (scripts evaluated, modules
/// loaded, job queue drains). Replaying the log in a fresh engine gives the same values, the same exceptions and the same report.
/// The byte format is documented in <c>docs/record-log.md</c> and starts with a version number.
/// </summary>
public sealed class RecordLog
{
    /// <summary>The version of the byte format this build writes and reads.</summary>
    public const int FormatVersion = 1;

    private static readonly byte[] Magic = "OERL"u8.ToArray();

    private byte[] _buf = new byte[256];
    private int _len;

    /// <summary>Creates an empty log.</summary>
    /// <param name="maxBytes">The most the log may grow to; past it recording stops and <see cref="IsReplayable"/> turns false. Default 8 MiB.</param>
    public RecordLog(int maxBytes = 8 << 20) => MaxBytes = maxBytes;

    /// <summary>The size limit passed to the constructor.</summary>
    public int MaxBytes { get; }

    /// <summary>Number of bytes of events recorded so far (without the 5-byte header).</summary>
    public int Length => _len;

    /// <summary>Number of events recorded so far.</summary>
    public int EventCount { get; internal set; }

    /// <summary>
    /// False when the run did something the log cannot reproduce (a direct <see cref="ScriptEngine.Call"/>, a host-made promise, a
    /// second realm) or when the log outgrew <see cref="MaxBytes"/>; <see cref="NotReplayableReason"/> says which.
    /// </summary>
    public bool IsReplayable => NotReplayableReason is null;

    /// <summary>Why the log cannot be replayed, or null.</summary>
    public string? NotReplayableReason { get; internal set; }

    /// <summary>Serializes the log: the header (<c>OERL</c>, the version) then the events.</summary>
    /// <returns>A new byte array.</returns>
    public byte[] ToArray()
    {
        var result = new byte[Magic.Length + 1 + _len];
        Magic.CopyTo(result, 0);
        result[Magic.Length] = FormatVersion;
        Array.Copy(_buf, 0, result, Magic.Length + 1, _len);
        return result;
    }

    /// <summary>Reads a log written by <see cref="ToArray"/>.</summary>
    /// <param name="bytes">The serialized log.</param>
    /// <returns>The log, ready to replay.</returns>
    /// <exception cref="FormatException">The header is wrong or the version is not <see cref="FormatVersion"/>.</exception>
    public static RecordLog FromArray(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length + 1 || !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new FormatException("Not a record log: the header 'OERL' is missing.");
        }

        if (bytes[Magic.Length] != FormatVersion)
        {
            throw new FormatException("Unsupported record log version " + bytes[Magic.Length] + "; this build reads version " + FormatVersion + ".");
        }

        var log = new RecordLog(int.MaxValue);
        ReadOnlySpan<byte> body = bytes[(Magic.Length + 1)..];
        log._buf = body.ToArray();
        log._len = body.Length;
        var r = new LogReader(log);
        while (r.HasMore)
        {
            r.SkipEvent();
            log.EventCount++;
        }

        return log;
    }

    internal byte[] Buffer => _buf;

    // ---- writing ---------------------------------------------------------------------------------------

    internal bool Full => _len > MaxBytes || NotReplayableReason is not null;

    internal void MarkNotReplayable(string reason) => NotReplayableReason ??= reason;

    internal void Tag(LogTag tag)
    {
        if (_len > MaxBytes)
        {
            MarkNotReplayable("the log grew past its limit of " + MaxBytes + " bytes");
            return;
        }

        EventCount++;
        Byte((byte)tag);
    }

    internal void Byte(byte b)
    {
        if (_len == _buf.Length)
        {
            Array.Resize(ref _buf, _buf.Length * 2);
        }

        _buf[_len++] = b;
    }

    internal void Varint(ulong v)
    {
        while (v >= 0x80)
        {
            Byte((byte)(v | 0x80));
            v >>= 7;
        }

        Byte((byte)v);
    }

    internal void Zigzag(long v) => Varint((ulong)((v << 1) ^ (v >> 63)));

    internal void Double(double d)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(tmp, BitConverter.DoubleToInt64Bits(d));
        foreach (byte b in tmp)
        {
            Byte(b);
        }
    }

    internal void Str(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        Varint((ulong)n);
        while (_len + n > _buf.Length)
        {
            Array.Resize(ref _buf, _buf.Length * 2);
        }

        Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _len);
        _len += n;
    }
}

/// <summary>The kinds of event in a <see cref="RecordLog"/>.</summary>
internal enum LogTag : byte
{
    Eval = 1,
    Module = 2,
    Jobs = 3,
    Clock = 4,
    Mono = 5,
    Random = 6,
    ZoneId = 7,
    Locale = 8,
    ZoneOffset = 9,
    Resolve = 10,
    HostCall = 11,
    Settle = 12,
    Weak = 13,
    EntryEnd = 14,
    DefineFn = 15,
}

/// <summary>Reads events back from a log.</summary>
internal sealed class LogReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    private double _lastClock;

    internal LogReader(RecordLog log)
    {
        _buf = log.Buffer;
        _end = log.Length;
    }

    internal int Pos { get; private set; }

    internal bool HasMore => Pos < _end;

    internal string PeekStringAfterTag()
    {
        int save = Pos;
        Pos++;
        string v = Str();
        Pos = save;
        return v;
    }

    internal long PeekVarintAfterTag()
    {
        int save = Pos;
        Pos++;
        long v = (long)Varint();
        Pos = save;
        return v;
    }

    internal LogTag Peek => Pos < _end ? (LogTag)_buf[Pos] : 0;

    internal LogTag Tag() => (LogTag)Next();

    internal byte Next() => Pos < _end ? _buf[Pos++] : throw new FormatException("The record log ends in the middle of an event.");

    internal ulong Varint()
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            byte b = Next();
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new FormatException("Bad varint in the record log.");
            }
        }
    }

    internal long Zigzag()
    {
        ulong u = Varint();
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    internal double Double()
    {
        if (Pos + 8 > _end)
        {
            throw new FormatException("The record log ends in the middle of an event.");
        }

        double d = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_buf.AsSpan(Pos, 8)));
        Pos += 8;
        return d;
    }

    internal string Str()
    {
        int n = checked((int)Varint());
        if (Pos + n > _end)
        {
            throw new FormatException("The record log ends in the middle of an event.");
        }

        string s = Encoding.UTF8.GetString(_buf, Pos, n);
        Pos += n;
        return s;
    }

    /// <summary>Clock values are stored as the difference from the previous reading, which is small for a loop that reads the time.</summary>
    internal double Clock()
    {
        _lastClock += Zigzag();
        return _lastClock;
    }

    internal void SkipEvent()
    {
        LogTag t = Tag();
        switch (t)
        {
            case LogTag.Eval: Str(); Str(); break;
            case LogTag.Module: Str(); break;
            case LogTag.Jobs: break;
            case LogTag.Clock: if (Next() == 0) { Clock(); } else { Double(); } break;
            case LogTag.Mono: Double(); break;
            case LogTag.Random: Double(); break;
            case LogTag.ZoneId: case LogTag.Locale: Str(); break;
            case LogTag.ZoneOffset: Zigzag(); Next(); break;
            case LogTag.Resolve:
                if (Next() == 1)
                {
                    Str();
                    Str();
                }

                break;
            case LogTag.HostCall: Str(); SkipOutcome(); break;
            case LogTag.Settle: Varint(); SkipOutcome(); break;
            case LogTag.Weak:
                for (ulong n = Varint(); n > 0; n--)
                {
                    Varint();
                }

                break;
            case LogTag.EntryEnd: if (Next() == 1) { Str(); } break;
            case LogTag.DefineFn: Str(); Varint(); break;
            default: throw new FormatException("Unknown event tag " + (byte)t + " in the record log.");
        }
    }

    private void SkipOutcome()
    {
        switch (Next())
        {
            case 0: ValueCodec.Skip(this); break;
            case 1:
                Str(); Str(); Str();
                for (ulong n = Varint(); n > 0; n--)
                {
                    Str(); Str(); Varint();
                }

                break;
            case 2: ValueCodec.Skip(this); break;
            case 3: break;
            case 4: Varint(); break;
            default: throw new FormatException("Bad host call outcome in the record log.");
        }
    }
}
