using System.Numerics;

namespace RecursiveAI.OneEcma;

/// <summary>
/// BigInt arithmetic with its cost accounted. <see cref="BigInteger"/> kernels run for as long as their operands are large and cannot
/// be interrupted, so every operation whose cost grows with operand size first charges fuel (and the allocation budget for its
/// result) in proportion to the work it is about to do. Multiplication, the building block of <c>**</c>, runs in pieces and polls
/// cancellation and the wall clock between them; the remaining kernels (division, small decimal conversions) are refused up front
/// when one run would be too long (<see cref="Agent.MaxUninterruptibleWork"/>) or could not end before the deadline.
/// ECMA-262 leaves the size limits of BigInt implementation-defined (6.1.6.2).
/// </summary>
internal static class BigMath
{
    /// <summary>Operands up to this many 32-bit words (together) are computed without any accounting.</summary>
    private const int SmallWords = 64;

    /// <summary>Largest operand, in bytes, multiplied in one BCL call (a few tens of milliseconds); bigger ones are split until the pieces are this size.</summary>
    internal static int BlockBytes = 1 << 17;

    internal static long Words(BigInteger x) => (x.GetBitLength() + 31) >> 5;

    /// <summary>Steps for multiplying operands of <paramref name="a"/> and <paramref name="b"/> words: a quadratic bound, deliberately above the measured time.</summary>
    internal static long MulSteps(long a, long b)
    {
        long m = Math.Min(a, b);
        long big = Math.Max(a, b);
        return (m * big / 400) + (big / 8) + 16;
    }

    /// <summary>Charges for a linear pass over a big operand (add, negate, mask, shift) and for the result it allocates.</summary>
    internal static void ChargeLinear(BigInteger x)
    {
        long w = Words(x);
        if (w > SmallWords && Agent.CurrentOrNull is { } ag)
        {
            ag.Alloc((w * 4) + 32);
            ag.ChargeWork(w >> 3, true);
        }
    }

    /// <summary>Charges for a binary linear operation (+, -, &amp;, |, ^).</summary>
    internal static void ChargeLinear(BigInteger x, BigInteger y)
    {
        long w = Math.Max(Words(x), Words(y));
        if (w > SmallWords && Agent.CurrentOrNull is { } ag)
        {
            ag.Alloc((w * 4) + 32);
            ag.ChargeWork(w >> 3, true);
        }
    }

    internal static BigInteger Neg(BigInteger x)
    {
        ChargeLinear(x);
        return -x;
    }

    internal static BigInteger Step(BigInteger x, int by)
    {
        ChargeLinear(x);
        return x + by;
    }

    // ---- multiplication -------------------------------------------------------------------------------

    internal static BigInteger Multiply(BigInteger x, BigInteger y)
    {
        long wx = Words(x);
        long wy = Words(y);
        if (wx + wy <= SmallWords || Agent.CurrentOrNull is not { } ag)
        {
            return x * y;
        }

        // 6.1.6.2.4: charge the result and the work before computing anything.
        ag.Alloc(((wx + wy) * 4) + 32);
        ag.ChargeWork(MulSteps(wx, wy), true);
        return MultiplyCore(ag, x, y);
    }

    private static BigInteger MultiplyCore(Agent ag, BigInteger x, BigInteger y)
    {
        if (x.IsZero || y.IsZero)
        {
            return BigInteger.Zero;
        }

        if (Words(x) * 4 <= BlockBytes && Words(y) * 4 <= BlockBytes)
        {
            return x * y;
        }

        byte[] a = BigInteger.Abs(x).ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] b = BigInteger.Abs(y).ToByteArray(isUnsigned: true, isBigEndian: false);
        BigInteger product = Karatsuba(ag, a, b);
        return (x.Sign < 0) != (y.Sign < 0) ? -product : product;
    }

    // Karatsuba over the operands' little-endian bytes. The BCL multiplies a pair no bigger than BlockBytes in one call (a few tens
    // of milliseconds); anything bigger is split until the pieces are that size, and cancellation and the wall clock are polled
    // before every such call. The recursion halves the larger operand each level, so its depth is the log of the size ratio.
    private static BigInteger Karatsuba(Agent ag, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < b.Length)
        {
            ReadOnlySpan<byte> t = a;
            a = b;
            b = t;
        }

        if (a.Length <= BlockBytes)
        {
            ag.CheckInterrupts();
            return new BigInteger(a, isUnsigned: true) * new BigInteger(b, isUnsigned: true);
        }

        int k = (a.Length + 1) / 2;
        if (b.Length <= k)
        {
            // Unbalanced: the product is a sum of two shorter products.
            BigInteger low = Karatsuba(ag, a[..k], b);
            BigInteger high = Karatsuba(ag, a[k..], b);
            return (high << (8 * k)) + low;
        }

        ReadOnlySpan<byte> a0 = a[..k];
        ReadOnlySpan<byte> a1 = a[k..];
        ReadOnlySpan<byte> b0 = b[..k];
        ReadOnlySpan<byte> b1 = b[k..];
        BigInteger z0 = Karatsuba(ag, a0, b0);
        BigInteger z2 = Karatsuba(ag, a1, b1);
        byte[] sa = (new BigInteger(a0, isUnsigned: true) + new BigInteger(a1, isUnsigned: true)).ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] sb = (new BigInteger(b0, isUnsigned: true) + new BigInteger(b1, isUnsigned: true)).ToByteArray(isUnsigned: true, isBigEndian: false);
        BigInteger z1 = Karatsuba(ag, sa, sb) - z0 - z2;
        return (z2 << (16 * k)) + (z1 << (8 * k)) + z0;
    }

    // ---- division -------------------------------------------------------------------------------------

    internal static BigInteger Divide(BigInteger x, BigInteger y)
    {
        ChargeDivision(x, y);
        return BigInteger.Divide(x, y);
    }

    internal static BigInteger Remainder(BigInteger x, BigInteger y)
    {
        ChargeDivision(x, y);
        return BigInteger.Remainder(x, y);
    }

    private static void ChargeDivision(BigInteger x, BigInteger y)
    {
        long wx = Words(x);
        long wy = Words(y);
        if (wx <= SmallWords || Agent.CurrentOrNull is not { } ag)
        {
            return;
        }

        long q = Math.Max(wx - wy, 0) + 1;
        ag.Alloc(((q + wy) * 4) + 32);
        ag.ChargeWork((q * wy / 300) + wx, false);
    }

    // ---- exponentiation -------------------------------------------------------------------------------

    /// <summary>x ** y for 0 &lt;= y &lt;= 10,000,000 (6.1.6.2.3), by square-and-multiply over <see cref="MultiplyCore"/>.</summary>
    internal static BigInteger Pow(BigInteger x, int y)
    {
        if (y <= 1 || x.IsZero || x.IsOne)
        {
            return BigInteger.Pow(x, y);
        }

        BigInteger ax = BigInteger.Abs(x);
        bool negative = x.Sign < 0 && (y & 1) == 1;
        long bitsX = (long)ax.GetBitLength();
        long bits = bitsX * y;
        if (bits >= (1L << 31))
        {
            throw Errors.Range("Maximum BigInt size exceeded");
        }

        Agent? ag = Agent.CurrentOrNull;
        long finalWords = (bits >> 5) + 1;
        if ((ax & (ax - 1)).IsZero)
        {
            // A power of two: the result is a single bit.
            ag?.Alloc((bits / 8) + 32);
            ag?.ChargeWork(finalWords >> 3, true);
            BigInteger p = BigInteger.One << (int)((bitsX - 1) * y);
            return negative ? -p : p;
        }

        if (finalWords <= SmallWords || ag is null)
        {
            return BigInteger.Pow(x, y);
        }

        // The last squaring dominates; all squarings together cost about a third more.
        ag.Alloc((finalWords * 4) + 32);
        ag.ChargeWork(MulSteps(finalWords / 2, finalWords / 2) * 4 / 3, true);
        BigInteger r = BigInteger.One;
        for (int bit = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)y); bit >= 0; bit--)
        {
            r = r.IsOne ? r : MultiplyCore(ag, r, r);
            if (((y >> bit) & 1) != 0)
            {
                r = MultiplyCore(ag, r, ax);
            }

            ag.CheckInterrupts();
        }

        return negative ? -r : r;
    }

    // ---- shifts and masks ------------------------------------------------------------------------------

    /// <summary>Charges a shift of <paramref name="x"/> by <paramref name="n"/> bits (left when positive) before it runs.</summary>
    internal static void ChargeShift(BigInteger x, long n)
    {
        long bits = (long)x.GetBitLength() + Math.Max(n, 0);
        long w = bits >> 5;
        if (w > SmallWords && Agent.CurrentOrNull is { } ag)
        {
            ag.Alloc((bits / 8) + 32);
            ag.ChargeWork(w >> 3, true);
        }
    }

    // ---- conversion to and from text ---------------------------------------------------------------------

    /// <summary>Charges for turning a BigInt into decimal digits with a kernel that cannot be interrupted (about quadratic in the size).</summary>
    internal static void ChargeDecimalConversion(BigInteger x)
    {
        long w = Words(x);
        if (w > 2048 && Agent.CurrentOrNull is { } ag)
        {
            ag.Alloc((w * 32 * 3 / 5) + 32);
            ag.ChargeWork(w * w / 11, false);
        }
    }

    /// <summary>Steps to parse <paramref name="digits"/> digits by chunked accumulation: quadratic, because every chunk touches the whole running value.</summary>
    internal static long ParseSteps(long digits) => (digits * digits / 2048) + (digits / 8);

    /// <summary>
    /// The value of <paramref name="digits"/> (each already known to be a valid digit of <paramref name="radix"/>) as an exact
    /// integer, charging the quadratic work before it starts and polling between chunks.
    /// </summary>
    internal static BigInteger ParseDigits(ReadOnlySpan<char> digits, int radix, Func<char, int> digitValue)
    {
        Agent? ag = Agent.CurrentOrNull;
        if (digits.Length > 64 && ag is not null)
        {
            ag.Alloc((digits.Length * 4L) + 32);
            ag.ChargeWork(ParseSteps(digits.Length), true);
        }

        int chunk = 1;
        long chunkBase = radix;
        while (chunkBase * radix <= uint.MaxValue)
        {
            chunkBase *= radix;
            chunk++;
        }

        BigInteger v = BigInteger.Zero;
        int p = 0;
        int rounds = 0;
        while (p < digits.Length)
        {
            int take = Math.Min(chunk, digits.Length - p);
            long part = 0;
            long mul = 1;
            for (int i = 0; i < take; i++)
            {
                part = (part * radix) + digitValue(digits[p + i]);
                mul *= radix;
            }

            v = (v * mul) + part;
            p += take;
            if ((++rounds & 63) == 0)
            {
                ag?.CheckInterrupts();
            }
        }

        return v;
    }
}
