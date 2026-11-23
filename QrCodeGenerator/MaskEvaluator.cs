using System;
#if NETCOREAPP3_0_OR_GREATER
using System.Numerics;
#endif

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// Picks the mask pattern with the lowest ISO/IEC 18004 penalty score (runs, 2x2 blocks, finder-like
/// patterns, dark/light balance; the first mask wins ties). Rows and columns are packed into 64-bit
/// words so every rule is evaluated with shifts and popcounts instead of per-module branches.
/// </summary>
internal static class MaskEvaluator
{
    private const int PenaltyN1 = 3;
    private const int PenaltyN2 = 3;
    private const int PenaltyN3 = 40;
    private const int PenaltyN4 = 10;

    private const int MaxWords = 3;     // 177 modules fit in 3 x 64 bits
    private const int PatternPeriod = 12; // every mask pattern repeats every 12 lines

    // Mask pattern bits: [((mask * 2 + orientation) * 12 + line % 12) * 3 + word].
    // Orientation 0 = rows (line = y, bit = x), 1 = columns (line = x, bit = y).
    private static readonly ulong[] PatternTable = BuildPatternTable();

    /// <summary>True if the given mask inverts module (x, y) (before excluding function modules).</summary>
    internal static bool MaskInverts(int mask, int x, int y)
    {
        switch (mask)
        {
            case 0: return (x + y) % 2 == 0;
            case 1: return y % 2 == 0;
            case 2: return x % 3 == 0;
            case 3: return (x + y) % 3 == 0;
            case 4: return (x / 3 + y / 2) % 2 == 0;
            case 5: return x * y % 2 + x * y % 3 == 0;
            case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
            case 7: return ((x + y) % 2 + x * y % 3) % 2 == 0;
            default: throw new ArgumentOutOfRangeException(nameof(mask));
        }
    }

    private static ulong[] BuildPatternTable()
    {
        var table = new ulong[8 * 2 * PatternPeriod * MaxWords];
        for (int mask = 0; mask < 8; mask++)
            for (int orient = 0; orient < 2; orient++)
                for (int line = 0; line < PatternPeriod; line++)
                {
                    int baseIdx = ((mask * 2 + orient) * PatternPeriod + line) * MaxWords;
                    for (int bit = 0; bit < MaxWords * 64; bit++)
                    {
                        bool inv = orient == 0 ? MaskInverts(mask, bit, line) : MaskInverts(mask, line, bit);
                        if (inv) table[baseIdx + (bit >> 6)] |= 1UL << (bit & 63);
                    }
                }
        return table;
    }

    /// <summary>Returns the row-oriented mask pattern words for the given mask and row (bit x = column x).</summary>
    internal static ReadOnlySpan<ulong> RowPattern(int mask, int y)
        => new ReadOnlySpan<ulong>(PatternTable, ((mask * 2) * PatternPeriod + y % PatternPeriod) * MaxWords, MaxWords);

    /// <summary>
    /// Returns the best mask (0..7) for the unmasked symbol. Format modules in <paramref name="modules"/>
    /// must be light (except the always-dark module); <paramref name="formatBits"/> gives the 15-bit
    /// format word for each of the 8 masks, and <paramref name="formatPositions"/> the (x, y) of each
    /// format bit, two copies per bit: [bit * 4 + copy * 2 + (0 = x, 1 = y)].
    /// </summary>
    public static int ChooseBestMask(byte[] modules, byte[] isFunction, int size,
        ReadOnlySpan<int> formatBits, ReadOnlySpan<int> formatPositions)
    {
        int words = (size + 63) >> 6;
        int lineLen = size * words;
        var buf = new ulong[lineLen * 6];
        var baseRows = new Span<ulong>(buf, 0, lineLen);
        var dataRows = new Span<ulong>(buf, lineLen, lineLen);
        var baseCols = new Span<ulong>(buf, lineLen * 2, lineLen);
        var dataCols = new Span<ulong>(buf, lineLen * 3, lineLen);
        var rows = new Span<ulong>(buf, lineLen * 4, lineLen);
        var cols = new Span<ulong>(buf, lineLen * 5, lineLen);

        Pack(modules, size, words, baseRows, baseCols);
        Pack(isFunction, size, words, dataRows, dataCols);
        // Data mask = not function, within the symbol
        Span<ulong> valid = stackalloc ulong[MaxWords];
        FillValid(valid, size);
        for (int i = 0; i < lineLen; i++)
        {
            dataRows[i] = ~dataRows[i] & valid[i % words];
            dataCols[i] = ~dataCols[i] & valid[i % words];
        }

        var scorer = new Scorer(size, words);
        int best = 0, bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyPatterns(mask, 0, baseRows, dataRows, rows, size, words);
            ApplyPatterns(mask, 1, baseCols, dataCols, cols, size, words);
            int bits = formatBits[mask];
            for (int i = 0; i < 15; i++)
            {
                if (((bits >> i) & 1) == 0) continue;
                for (int copy = 0; copy < 2; copy++)
                {
                    int x = formatPositions[i * 4 + copy * 2], y = formatPositions[i * 4 + copy * 2 + 1];
                    rows[y * words + (x >> 6)] |= 1UL << (x & 63);
                    cols[x * words + (y >> 6)] |= 1UL << (y & 63);
                }
            }
            int score = scorer.Score(rows, cols, bestScore);
            if (score < bestScore)
            {
                bestScore = score;
                best = mask;
            }
        }
        return best;
    }

    /// <summary>Penalty score of a finished (masked) symbol. Used for testing.</summary>
    internal static int Score(byte[] modules, int size)
    {
        int words = (size + 63) >> 6;
        var rows = new ulong[size * words];
        var cols = new ulong[size * words];
        Pack(modules, size, words, rows, cols);
        return new Scorer(size, words).Score(rows, cols, int.MaxValue);
    }

    private static void Pack(byte[] src, int size, int words, Span<ulong> rows, Span<ulong> cols)
    {
        for (int y = 0; y < size; y++)
        {
            int row = y * size;
            // Rows: accumulate 64 modules per word in a register
            for (int w = 0, x = 0; x < size; w++)
            {
                ulong acc = 0;
                int end = Math.Min(size, x + 64);
                for (int b = 0; x < end; x++, b++)
                    acc |= (ulong)(src[row + x] & 1u) << b;
                rows[y * words + w] = acc;
            }
            // Columns: scatter bit y of each column
            int colWord = y >> 6, colBit = y & 63;
            for (int x = 0, k = colWord; x < size; x++, k += words)
                cols[k] |= (ulong)(src[row + x] & 1u) << colBit;
        }
    }

    private static void ApplyPatterns(int mask, int orient, ReadOnlySpan<ulong> baseLines, ReadOnlySpan<ulong> data,
        Span<ulong> dst, int size, int words)
    {
        int tableBase = (mask * 2 + orient) * PatternPeriod * MaxWords;
        for (int line = 0, k = 0; line < size; line++)
        {
            int p = tableBase + (line % PatternPeriod) * MaxWords;
            for (int w = 0; w < words; w++, k++)
                dst[k] = baseLines[k] ^ (PatternTable[p + w] & data[k]);
        }
    }

    // Sets bits [0, count) of a multi-word bit set.
    private static void FillValid(Span<ulong> dst, int count)
    {
        dst.Clear();
        for (int w = 0; w < dst.Length; w++)
        {
            int bits = count - w * 64;
            if (bits >= 64) dst[w] = ulong.MaxValue;
            else if (bits > 0) dst[w] = (1UL << bits) - 1;
        }
    }

    private readonly struct Scorer
    {
        private readonly int _size, _words;
        private readonly ulong _v1a, _v1b, _v1c; // positions x <= size - 2
        private readonly ulong _v5a, _v5b, _v5c; // positions x <= size - 5

        public Scorer(int size, int words)
        {
            _size = size;
            _words = words;
            Span<ulong> v = stackalloc ulong[MaxWords];
            FillValid(v, size - 1);
            _v1a = v[0]; _v1b = v[1]; _v1c = v[2];
            FillValid(v, size - 4);
            _v5a = v[0]; _v5b = v[1]; _v5c = v[2];
        }

        private ulong V1(int w) => w == 0 ? _v1a : w == 1 ? _v1b : _v1c;
        private ulong V5(int w) => w == 0 ? _v5a : w == 1 ? _v5b : _v5c;

        public int Score(ReadOnlySpan<ulong> rows, ReadOnlySpan<ulong> cols, int limit)
        {
            int size = _size, words = _words;
            int result = 0;

            // N4: balance of dark and light modules
            int dark = 0;
            foreach (ulong v in rows) dark += Bits.PopCount(v);
            int total = size * size;
            // Deviation from 50% dark in units of 0.5%; free up to 5%, then 10 points per started 5% step.
            // The symbol has an odd number of modules, so the deviation is never 0.
            int deviation = Math.Abs(dark * 20 - total * 10);
            result += (deviation - 1) / total * PenaltyN4;

            // N2: 2x2 blocks of one color
            int blocks = 0;
            for (int y = 0; y < size - 1; y++)
            {
                int r0 = y * words, r1 = r0 + words;
                for (int w = 0; w < words; w++)
                {
                    ulong a = rows[r0 + w], b = rows[r1 + w];
                    ulong an = w + 1 < words ? rows[r0 + w + 1] : 0;
                    ulong bn = w + 1 < words ? rows[r1 + w + 1] : 0;
                    ulong a1 = (a >> 1) | (an << 63);
                    ulong b1 = (b >> 1) | (bn << 63);
                    blocks += Bits.PopCount(~((a ^ a1) | (b ^ b1) | (a ^ b)) & V1(w));
                }
            }
            result += blocks * PenaltyN2;
            if (result >= limit) return result;

            // N1 + N3 over rows, then columns
            for (int i = 0; i < rows.Length; i += words)
                result += LinePenalty(rows.Slice(i, words));
            if (result >= limit) return result;
            for (int i = 0; i < cols.Length; i += words)
                result += LinePenalty(cols.Slice(i, words));
            return result;
        }

        private int LinePenalty(ReadOnlySpan<ulong> line)
        {
            int words = _words;
            int runPenalty = 0, finders = 0;
            bool maybeScaledFinder1 = false, maybeScaledFinder2 = false;
            ulong prevWin = 0;

            for (int w = 0; w < words; w++)
            {
                ulong prev = w > 0 ? line[w - 1] : 0;
                ulong cur = line[w];
                ulong next = w + 1 < words ? line[w + 1] : 0;

                // pK has bit x = module x + K; mK has bit x = module x - K (outside the line = light)
                ulong p1 = (cur >> 1) | (next << 63);
                ulong p2 = (cur >> 2) | (next << 62);
                ulong p3 = (cur >> 3) | (next << 61);
                ulong p4 = (cur >> 4) | (next << 60);
                ulong p5 = (cur >> 5) | (next << 59);
                ulong p6 = (cur >> 6) | (next << 58);
                ulong p7 = (cur >> 7) | (next << 57);
                ulong m1 = (cur << 1) | (prev >> 63);

                // N1: a run of length L >= 5 costs N1 + (L - 5) = (number of 5-module windows: L - 4) + (N1 - 1)
                ulong win = ~((cur ^ p1) | (p1 ^ p2) | (p2 ^ p3) | (p3 ^ p4)) & V5(w);
                ulong runStarts = win & ~((win << 1) | (prevWin >> 63));
                runPenalty += Bits.PopCount(win) + (PenaltyN1 - 1) * Bits.PopCount(runStarts);
                prevWin = win;

                // N3 with module size n = 1: light, D L DDD L D, light, needing 4 light on one side
                ulong core = ~m1 & cur & ~p1 & p2 & p3 & p4 & ~p5 & p6 & ~p7;
                if (core != 0)
                {
                    ulong m2 = (cur << 2) | (prev >> 62);
                    ulong m3 = (cur << 3) | (prev >> 61);
                    ulong m4 = (cur << 4) | (prev >> 60);
                    ulong p8 = (cur >> 8) | (next << 56);
                    ulong p9 = (cur >> 9) | (next << 55);
                    ulong p10 = (cur >> 10) | (next << 54);
                    finders += Bits.PopCount(core & ~(m2 | m3 | m4)) + Bits.PopCount(core & ~(p8 | p9 | p10));
                }

                // Patterns with n >= 2 contain "LL DDDDDD" and "DDDDDD LL"; only then do the exact run scan
                ulong d6 = p2 & p3 & p4 & p5 & p6 & p7;
                maybeScaledFinder1 |= (~cur & ~p1 & d6) != 0;
                maybeScaledFinder2 |= (cur & p1 & p2 & p3 & p4 & p5 & ~p6 & ~p7) != 0;
            }

            if (maybeScaledFinder1 && maybeScaledFinder2)
                finders = CountFindersByRuns(line);
            return runPenalty + finders * PenaltyN3;
        }

        // Counts finder-like patterns of any unit size u: dark, light, dark, light, dark runs of
        // u, u, 3u, u, u modules with light of at least 4u on one side and at least u on the other.
        // Each side that has the 4u light area counts once. The quiet zone around the symbol is
        // treated as a light run of 'size' modules, which is wider than any 4u that fits the line.
        private int CountFindersByRuns(ReadOnlySpan<ulong> line)
        {
            int size = _size, words = _words;

            // Light runs sit at even indices, dark runs at odd ones; the list starts and ends light.
            Span<int> runs = stackalloc int[size + 2];
            runs[0] = size;
            int count = 1;
            bool dark = (line[0] & 1) != 0;
            int start = 0;
            for (int w = 0; w <= words; w++)
            {
                ulong ends;
                if (w < words)
                {
                    ulong cur = line[w];
                    ulong next = w + 1 < words ? line[w + 1] : 0;
                    ends = (cur ^ ((cur >> 1) | (next << 63))) & V1(w); // module x differs from x + 1
                }
                else
                {
                    ends = 1; // pseudo word: the last module always ends a run
                }
                while (ends != 0)
                {
                    int end = w < words ? w * 64 + Bits.TrailingZeroCount(ends) : size - 1;
                    ends &= ends - 1;
                    int length = end + 1 - start;
                    start = end + 1;
                    if (dark == ((count & 1) == 1)) runs[count++] = length; // last run (odd count = light) has the other color
                    else runs[count - 1] += length;                        // leading light joins the quiet zone
                    dark = !dark;
                }
            }
            if ((count & 1) == 0) runs[count++] = size; // ended dark: quiet zone is its own run
            else runs[count - 1] += size;               // ended light: extend into the quiet zone

            int found = 0;
            for (int i = 1; i + 5 < count; i += 2)
            {
                int u = runs[i];
                if (runs[i + 1] != u || runs[i + 2] != 3 * u || runs[i + 3] != u || runs[i + 4] != u) continue;
                int before = runs[i - 1], after = runs[i + 5];
                if (before >= 4 * u && after >= u) found++;
                if (after >= 4 * u && before >= u) found++;
            }
            return found;
        }
    }
}

/// <summary>Bit helpers with a portable fallback for netstandard.</summary>
internal static class Bits
{
#if NETCOREAPP3_0_OR_GREATER
    public static int PopCount(ulong v) => BitOperations.PopCount(v);
    public static int TrailingZeroCount(ulong v) => BitOperations.TrailingZeroCount(v);
#else
    public static int PopCount(ulong v)
    {
        v -= (v >> 1) & 0x5555555555555555UL;
        v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
        v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
        return (int)((v * 0x0101010101010101UL) >> 56);
    }

    private static readonly byte[] DeBruijn64 =
    {
        0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4,
        62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
        63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11,
        46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6,
    };

    public static int TrailingZeroCount(ulong v)
        => v == 0 ? 64 : DeBruijn64[((v & (ulong)-(long)v) * 0x03F79D71B4CB0A89UL) >> 58];
#endif
}
