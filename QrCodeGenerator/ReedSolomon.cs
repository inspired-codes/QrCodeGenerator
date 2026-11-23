using System;
using System.Threading;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// Reed-Solomon ECC over GF(2^8 / 0x11D).
/// The remainder register (at most 30 bytes) is kept in four 64-bit words, and for each generator
/// degree a 256-entry table holds generator * factor, so each input byte costs one shift and four XORs.
/// </summary>
internal static class ReedSolomon
{
    private const int MaxDegree = 30;

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    // Per degree: 256 rows x 4 words; row f = generator coefficients times f, big-endian bytes.
    private static readonly ulong[]?[] TableCache = new ulong[]?[MaxDegree + 1];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];
    }

    internal static int Multiply(int a, int b)
        => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    /// <summary>
    /// Generator polynomial g(x) = (x - a^0)(x - a^1)...(x - a^(degree-1)) with a = 2, as its coefficients
    /// from x^(degree-1) down to x^0 (the leading x^degree coefficient is always 1 and is left out).
    /// </summary>
    internal static byte[] ComputeDivisor(int degree)
    {
        if (degree < 1 || degree > MaxDegree) throw new ArgumentOutOfRangeException(nameof(degree));

        // g[0] is the highest power. Start with g(x) = 1 and multiply in one factor at a time;
        // in GF(2^8) subtraction is XOR, so (x - r) = (x + r).
        var g = new byte[degree + 1];
        g[0] = 1;
        for (int i = 0; i < degree; i++)
        {
            int r = Exp[i];
            for (int k = i + 1; k >= 1; k--)
                g[k] ^= (byte)Multiply(g[k - 1], r);
        }

        var divisor = new byte[degree];
        Array.Copy(g, 1, divisor, 0, degree);
        return divisor;
    }

    private static ulong[] GetTable(int degree)
    {
        ulong[]? table = Volatile.Read(ref TableCache[degree]);
        if (table != null) return table;

        byte[] divisor = ComputeDivisor(degree);
        table = new ulong[256 * 4];
        for (int f = 1; f < 256; f++)
            for (int i = 0; i < degree; i++)
                table[f * 4 + (i >> 3)] |= (ulong)Multiply(divisor[i], f) << (56 - 8 * (i & 7));
        // Benign race: every thread computes the same table. Volatile publication guarantees readers
        // on weak memory models never see the reference before the contents.
        Volatile.Write(ref TableCache[degree], table);
        return table;
    }

    /// <summary>Computes the RS remainder (the ECC bytes) of data for a generator of result.Length degree.</summary>
    public static void ComputeRemainder(ReadOnlySpan<byte> data, Span<byte> result)
    {
        int degree = result.Length;
        ulong[] table = GetTable(degree);

        // Remainder bytes 0..degree-1 as a big-endian 256-bit number r0:r1:r2:r3; bytes >= degree stay zero.
        ulong r0 = 0, r1 = 0, r2 = 0, r3 = 0;
        foreach (byte b in data)
        {
            int t = (b ^ (int)(r0 >> 56)) * 4;
            r0 = ((r0 << 8) | (r1 >> 56)) ^ table[t];
            r1 = ((r1 << 8) | (r2 >> 56)) ^ table[t + 1];
            r2 = ((r2 << 8) | (r3 >> 56)) ^ table[t + 2];
            r3 = (r3 << 8) ^ table[t + 3];
        }

        for (int i = 0; i < degree; i++)
        {
            ulong word = (i >> 3) switch { 0 => r0, 1 => r1, 2 => r2, _ => r3 };
            result[i] = (byte)(word >> (56 - 8 * (i & 7)));
        }
    }
}
