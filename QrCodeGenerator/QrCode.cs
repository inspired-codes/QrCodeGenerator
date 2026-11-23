using System;
using System.Collections.Generic;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// An immutable QR Code (Model 2, ISO/IEC 18004) as a square grid of dark and light modules.
/// <para>Create one with <see cref="EncodeText"/>, <see cref="EncodeBinary"/> or, for full control,
/// <see cref="EncodeSegments"/>. All 40 symbol versions and all four error correction levels are supported.
/// Instances are immutable and can be shared freely between threads.</para>
/// </summary>
public sealed partial class QrCode
{
    /// <summary>Smallest symbol version (21 x 21 modules).</summary>
    public const int MinVersion = 1;

    /// <summary>Largest symbol version (177 x 177 modules).</summary>
    public const int MaxVersion = 40;

    /*---- Encoding entry points ----*/

    /// <summary>
    /// Encodes text in the most compact single mode that can represent it (numeric, alphanumeric,
    /// otherwise UTF-8 bytes), using the smallest version that fits. The error correction level is
    /// raised above <paramref name="ecl"/> whenever that is possible without a larger symbol.
    /// </summary>
    /// <exception cref="DataTooLongException">The text does not fit in a version 40 symbol.</exception>
    public static QrCode EncodeText(string text, Ecc ecl)
        => EncodeSegments(QrSegment.MakeSegments(text), ecl);

    /// <summary>
    /// Encodes arbitrary bytes in byte mode, using the smallest version that fits. The error correction
    /// level is raised above <paramref name="ecl"/> whenever that is possible without a larger symbol.
    /// </summary>
    /// <exception cref="DataTooLongException">The data does not fit in a version 40 symbol.</exception>
    public static QrCode EncodeBinary(byte[] data, Ecc ecl)
        => EncodeSegments(new[] { QrSegment.MakeBytes(data) }, ecl);

    /// <summary>
    /// Encodes a sequence of segments.
    /// </summary>
    /// <param name="segs">Segments to concatenate, in order.</param>
    /// <param name="ecl">Minimum error correction level.</param>
    /// <param name="minVersion">Smallest version to consider.</param>
    /// <param name="maxVersion">Largest version to consider.</param>
    /// <param name="mask">Mask pattern 0 to 7, or -1 to pick the pattern with the lowest penalty score.</param>
    /// <param name="boostEcl">Whether to use the highest error correction level that still fits the chosen version.</param>
    /// <exception cref="DataTooLongException">The segments do not fit in <paramref name="maxVersion"/>.</exception>
    public static QrCode EncodeSegments(IReadOnlyList<QrSegment> segs, Ecc ecl,
        int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1, bool boostEcl = true)
    {
        if (segs == null) throw new ArgumentNullException(nameof(segs));
        CheckEcc(ecl);
        if (minVersion < MinVersion || maxVersion > MaxVersion || minVersion > maxVersion)
            throw new ArgumentOutOfRangeException(nameof(minVersion), "Invalid version range");
        if (mask < -1 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask));

        int version = minVersion;
        int bitsNeeded;
        while (true)
        {
            bitsNeeded = QrSegment.GetTotalBits(segs, version);
            int capacity = GetNumDataCodewords(version, ecl) * 8;
            if (bitsNeeded >= 0 && bitsNeeded <= capacity) break;
            if (version == maxVersion)
                throw new DataTooLongException(bitsNeeded < 0
                    ? "A segment has more characters than its length field can express"
                    : $"Data needs {bitsNeeded} bits, but version {maxVersion} at level {ecl} holds only {capacity} bits");
            version++;
        }

        // Capacity shrinks as the level rises, so stop at the first level that no longer fits.
        if (boostEcl)
            while (ecl < Ecc.High && bitsNeeded <= GetNumDataCodewords(version, ecl + 1) * 8)
                ecl++;

        return new QrCode(version, ecl, BuildDataCodewords(segs, version, ecl), mask);
    }

    // Mode indicators, character counts and payloads, followed by the terminator and pad codewords.
    private static byte[] BuildDataCodewords(IReadOnlyList<QrSegment> segs, int version, Ecc ecl)
    {
        int byteCount = GetNumDataCodewords(version, ecl);
        int capacityBits = byteCount * 8;
        var bits = new BitBuffer(capacityBits);
        for (int i = 0; i < segs.Count; i++)
        {
            QrSegment seg = segs[i];
            bits.AppendBits(seg.Mode.ModeBits, 4);
            bits.AppendBits(seg.NumChars, seg.Mode.NumCharCountBits(version));
            bits.AppendData(seg.Data);
        }

        // The terminator (up to four 0 bits) and the zero bits up to the next byte boundary are already
        // zero in the buffer; only the alternating pad codewords 11101100 / 00010001 need writing.
        int terminated = Math.Min(capacityBits, bits.BitLength + 4);
        int used = (terminated + 7) / 8;
        var result = new byte[byteCount];
        Buffer.BlockCopy(bits.RawBytes, 0, result, 0, used);
        for (int i = used; i < byteCount; i++)
            result[i] = (byte)(((i - used) & 1) == 0 ? 0xEC : 0x11);
        return result;
    }

    /*---- Properties ----*/

    /// <summary>Symbol version, 1 to 40.</summary>
    public int Version { get; }

    /// <summary>Modules per side: 4 * <see cref="Version"/> + 17, so 21 to 177.</summary>
    public int Size { get; }

    /// <summary>Error correction level actually used (may be higher than requested).</summary>
    public Ecc ErrorCorrectionLevel { get; }

    /// <summary>Mask pattern applied to the data area, 0 to 7.</summary>
    public int Mask { get; }

    // Row-major module grid, 1 = dark.
    private readonly byte[] _modules;

    /*---- Construction ----*/

    /// <summary>
    /// Builds a symbol from complete data codewords (payload, terminator and padding already included).
    /// Most callers want <see cref="EncodeSegments"/> instead.
    /// </summary>
    /// <param name="version">Symbol version, 1 to 40.</param>
    /// <param name="ecl">Error correction level.</param>
    /// <param name="dataCodewords">Exactly <see cref="GetNumDataCodewords"/> bytes.</param>
    /// <param name="mask">Mask pattern 0 to 7, or -1 to pick the pattern with the lowest penalty score.</param>
    public QrCode(int version, Ecc ecl, byte[] dataCodewords, int mask)
    {
        if (version < MinVersion || version > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version));
        CheckEcc(ecl);
        if (mask < -1 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask));
        if (dataCodewords == null) throw new ArgumentNullException(nameof(dataCodewords));
        if (dataCodewords.Length != GetNumDataCodewords(version, ecl))
            throw new ArgumentException("Wrong number of data codewords", nameof(dataCodewords));

        Version = version;
        ErrorCorrectionLevel = ecl;
        Size = version * 4 + 17;

        int cells = Size * Size;
        var modules = new byte[cells];
        var reserved = new byte[cells]; // 1 = function module, never masked
        int[] formatPositions = GetFormatPositions(Size);

        DrawFunctionPatterns(modules, reserved, formatPositions);
        PlaceCodewords(InterleaveWithEcc(dataCodewords), modules, reserved);

        if (mask == -1)
        {
            Span<int> formatWords = stackalloc int[8];
            for (int m = 0; m < 8; m++)
                formatWords[m] = FormatWord(ecl, m);
            mask = MaskEvaluator.ChooseBestMask(modules, reserved, Size, formatWords, formatPositions);
        }
        ApplyMask(modules, reserved, mask);
        DrawFormatInfo(modules, null, formatPositions, FormatWord(ecl, mask));

        _modules = modules;
        Mask = mask;
    }

    /*---- Module access ----*/

    /// <summary>
    /// True if the module at column <paramref name="x"/>, row <paramref name="y"/> is dark.
    /// (0, 0) is the top left corner; coordinates outside the symbol are light.
    /// </summary>
    public bool GetModule(int x, int y)
        => (uint)x < (uint)Size && (uint)y < (uint)Size && _modules[y * Size + x] != 0;

    /// <summary>Copies the grid row by row into <paramref name="destination"/> (1 = dark, 0 = light); it needs Size * Size bytes.</summary>
    public void CopyModulesTo(Span<byte> destination) => _modules.AsSpan().CopyTo(destination);

    /*---- Function patterns ----*/

    private void DrawFunctionPatterns(byte[] m, byte[] reserved, int[] formatPositions)
    {
        int size = Size;

        // Timing patterns along row 6 and column 6, dark on even indices
        for (int i = 0; i < size; i++)
        {
            byte dark = (byte)((i & 1) == 0 ? 1 : 0);
            SetFunction(m, reserved, i, 6, dark);
            SetFunction(m, reserved, 6, i, dark);
        }

        // Finder patterns with their light separators in three corners
        DrawFinder(m, reserved, 3, 3);
        DrawFinder(m, reserved, size - 4, 3);
        DrawFinder(m, reserved, 3, size - 4);

        // Alignment patterns at every combination of the listed centres, except over the finders
        byte[] centres = AlignmentCentres[Version];
        int last = centres.Length - 1;
        for (int i = 0; i <= last; i++)
            for (int j = 0; j <= last; j++)
            {
                bool overFinder = (i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0);
                if (!overFinder)
                    DrawAlignment(m, reserved, centres[i], centres[j]);
            }

        // Reserve the format areas (filled in after masking) and draw the version blocks
        DrawFormatInfo(m, reserved, formatPositions, 0);
        DrawVersionInfo(m, reserved);
    }

    // 7x7 finder (dark ring, light ring, 3x3 dark core) plus the light separator around it.
    private void DrawFinder(byte[] m, byte[] reserved, int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            int y = cy + dy;
            if ((uint)y >= (uint)Size) continue;
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx;
                if ((uint)x >= (uint)Size) continue;
                int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(m, reserved, x, y, (byte)(ring <= 1 || ring == 3 ? 1 : 0));
            }
        }
    }

    // 5x5 alignment pattern: dark outer ring, light ring, dark centre.
    private void DrawAlignment(byte[] m, byte[] reserved, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(m, reserved, cx + dx, cy + dy, (byte)(ring == 1 ? 0 : 1));
            }
    }

    // Writes both copies of a 15-bit format word plus the dark module next to the lower left finder.
    // When 'reserved' is given, the modules are also marked as function modules.
    private void DrawFormatInfo(byte[] m, byte[]? reserved, int[] positions, int word)
    {
        for (int bit = 0; bit < 15; bit++)
        {
            byte value = (byte)((word >> bit) & 1);
            for (int copy = 0; copy < 2; copy++)
            {
                int k = bit * 4 + copy * 2;
                int i = positions[k + 1] * Size + positions[k];
                m[i] = value;
                if (reserved != null) reserved[i] = 1;
            }
        }
        int dark = (Size - 8) * Size + 8;
        m[dark] = 1;
        if (reserved != null) reserved[dark] = 1;
    }

    // Versions 7 and up carry an 18-bit version word in two 6x3 blocks next to the upper right and lower left finders.
    private void DrawVersionInfo(byte[] m, byte[] reserved)
    {
        if (Version < 7) return;
        int word = (Version << 12) | BchRemainder(Version, 0x1F25, 12);
        int far = Size - 11;
        for (int bit = 0; bit < 18; bit++)
        {
            byte value = (byte)((word >> bit) & 1);
            int along = bit / 3, across = far + bit % 3;
            SetFunction(m, reserved, across, along, value); // upper right block
            SetFunction(m, reserved, along, across, value); // lower left block
        }
    }

    private void SetFunction(byte[] m, byte[] reserved, int x, int y, byte dark)
    {
        int i = y * Size + x;
        m[i] = dark;
        reserved[i] = 1;
    }

    // Module coordinates of each format bit: [bit * 4 + copy * 2 + (0 = x, 1 = y)].
    // Copy 0 wraps around the upper left finder, copy 1 is split between the other two finders.
    private static int[] GetFormatPositions(int size)
    {
        ReadOnlySpan<byte> nearX = new byte[] { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
        ReadOnlySpan<byte> nearY = new byte[] { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };
        var positions = new int[60];
        for (int bit = 0; bit < 15; bit++)
        {
            positions[bit * 4] = nearX[bit];
            positions[bit * 4 + 1] = nearY[bit];
            bool beside = bit < 8; // bits 0-7 run leftwards below the upper right finder
            positions[bit * 4 + 2] = beside ? size - 1 - bit : 8;
            positions[bit * 4 + 3] = beside ? 8 : size - 15 + bit;
        }
        return positions;
    }

    /*---- Data placement and masking ----*/

    // Splits the data into blocks, appends Reed-Solomon ECC to each block and interleaves
    // the codewords column by column across blocks (data first, then ECC).
    private byte[] InterleaveWithEcc(byte[] data)
    {
        int level = (int)ErrorCorrectionLevel;
        int eccPerBlock = BlockTable[Version, level * 2];
        int blocks = BlockTable[Version, level * 2 + 1];
        int total = CountDataModules(Version) / 8;
        int longBlocks = total % blocks;            // the last blocks carry one extra data codeword
        int shortBlocks = blocks - longBlocks;
        int shortData = total / blocks - eccPerBlock;

        var result = new byte[total + 1]; // spare zero byte: remainder bits come out light
        Span<byte> ecc = stackalloc byte[eccPerBlock];
        int offset = 0;
        for (int b = 0; b < blocks; b++)
        {
            int length = b < shortBlocks ? shortData : shortData + 1;
            var block = new ReadOnlySpan<byte>(data, offset, length);
            offset += length;

            for (int i = 0; i < shortData; i++)
                result[i * blocks + b] = block[i];
            if (length > shortData)
                result[shortData * blocks + (b - shortBlocks)] = block[shortData];

            ReedSolomon.ComputeRemainder(block, ecc);
            for (int i = 0; i < eccPerBlock; i++)
                result[data.Length + i * blocks + b] = ecc[i];
        }
        return result;
    }

    // Fills the non-function modules two columns at a time from the right edge, alternating between
    // upward and downward sweeps and skipping the vertical timing column. Branch-free: 'data' ends
    // with a zero byte that supplies the light remainder bits.
    private void PlaceCodewords(byte[] data, byte[] m, byte[] reserved)
    {
        int size = Size;
        int bit = 0;
        bool upward = true;
        for (int right = size - 1; right > 0; right -= 2)
        {
            if (right == 6) right = 5;
            int i = upward ? (size - 1) * size + right : right;
            int step = upward ? -size : size;
            for (int n = 0; n < size; n++, i += step)
            {
                for (int x = 0; x < 2; x++)
                {
                    int free = reserved[i - x] ^ 1;
                    m[i - x] |= (byte)((data[bit >> 3] >> (7 - (bit & 7))) & free);
                    bit += free;
                }
            }
            upward = !upward;
        }
    }

    // XORs the mask pattern onto the data area; applying it twice restores the original.
    private void ApplyMask(byte[] m, byte[] reserved, int mask)
    {
        int size = Size;
        for (int y = 0; y < size; y++)
        {
            ReadOnlySpan<ulong> pattern = MaskEvaluator.RowPattern(mask, y);
            int row = y * size;
            for (int w = 0, x = 0; x < size; w++)
            {
                ulong bits = pattern[w];
                int end = Math.Min(size, x + 64);
                for (int i = row + x; x < end; x++, i++, bits >>= 1)
                    m[i] ^= (byte)((uint)bits & (uint)(reserved[i] ^ 1) & 1u);
            }
        }
    }

    /*---- Capacity and code words ----*/

    /// <summary>Number of 8-bit data codewords a symbol of the given version and level holds.</summary>
    public static int GetNumDataCodewords(int version, Ecc ecl)
    {
        if (version < MinVersion || version > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version));
        CheckEcc(ecl);
        int level = (int)ecl;
        return CountDataModules(version) / 8 - BlockTable[version, level * 2] * BlockTable[version, level * 2 + 1];
    }

    // Modules left for codewords once every function pattern is drawn (may include 0-7 remainder bits).
    private static int CountDataModules(int version)
    {
        int size = version * 4 + 17;
        int n = AlignmentCentres[version].Length;
        int alignments = n == 0 ? 0 : n * n - 3;
        int timingOverlap = n > 2 ? 2 * (n - 2) * 5 : 0; // alignments centred on row/column 6 cover 5 timing modules each
        int function = 3 * 8 * 8                            // finders including separators
            + 2 * (size - 16)                                 // timing patterns between the separators
            + 2 * 15 + 1                                      // format information and the dark module
            + 25 * alignments - timingOverlap
            + (version >= 7 ? 2 * 18 : 0);                    // version information
        return size * size - function;
    }

    // 15-bit format word: 2 level bits and 3 mask bits, BCH(15,5) protected, XOR 101010000010010.
    private static int FormatWord(Ecc ecl, int mask)
    {
        int levelBits = ecl switch { Ecc.Low => 0b01, Ecc.Medium => 0b00, Ecc.Quartile => 0b11, _ => 0b10 };
        int data = levelBits << 3 | mask;
        return ((data << 10) | BchRemainder(data, 0x537, 10)) ^ 0x5412;
    }

    // Remainder of value * x^degree divided by the generator polynomial over GF(2).
    // Used for format (BCH 15,5) and version (BCH 18,6) information; value has at most 6 bits.
    private static int BchRemainder(int value, int generator, int degree)
    {
        int r = value << degree;
        for (int shift = 5; shift >= 0; shift--)
            if ((r >> (shift + degree) & 1) != 0)
                r ^= generator << shift;
        return r;
    }

    private static void CheckEcc(Ecc ecl)
    {
        if ((uint)ecl > (uint)Ecc.High) throw new ArgumentOutOfRangeException(nameof(ecl));
    }

    /*---- Tables from ISO/IEC 18004 ----*/

    // Per version: ECC codewords per block and number of blocks, for levels L, M, Q, H. Row 0 is unused.
    private static readonly byte[,] BlockTable =
    {
        {  0,  0,  0,  0,  0,  0,  0,  0 },
        {  7,  1, 10,  1, 13,  1, 17,  1 }, // 1
        { 10,  1, 16,  1, 22,  1, 28,  1 }, // 2
        { 15,  1, 26,  1, 18,  2, 22,  2 }, // 3
        { 20,  1, 18,  2, 26,  2, 16,  4 }, // 4
        { 26,  1, 24,  2, 18,  4, 22,  4 }, // 5
        { 18,  2, 16,  4, 24,  4, 28,  4 }, // 6
        { 20,  2, 18,  4, 18,  6, 26,  5 }, // 7
        { 24,  2, 22,  4, 22,  6, 26,  6 }, // 8
        { 30,  2, 22,  5, 20,  8, 24,  8 }, // 9
        { 18,  4, 26,  5, 24,  8, 28,  8 }, // 10
        { 20,  4, 30,  5, 28,  8, 24, 11 }, // 11
        { 24,  4, 22,  8, 26, 10, 28, 11 }, // 12
        { 26,  4, 22,  9, 24, 12, 22, 16 }, // 13
        { 30,  4, 24,  9, 20, 16, 24, 16 }, // 14
        { 22,  6, 24, 10, 30, 12, 24, 18 }, // 15
        { 24,  6, 28, 10, 24, 17, 30, 16 }, // 16
        { 28,  6, 28, 11, 28, 16, 28, 19 }, // 17
        { 30,  6, 26, 13, 28, 18, 28, 21 }, // 18
        { 28,  7, 26, 14, 26, 21, 26, 25 }, // 19
        { 28,  8, 26, 16, 30, 20, 28, 25 }, // 20
        { 28,  8, 26, 17, 28, 23, 30, 25 }, // 21
        { 28,  9, 28, 17, 30, 23, 24, 34 }, // 22
        { 30,  9, 28, 18, 30, 25, 30, 30 }, // 23
        { 30, 10, 28, 20, 30, 27, 30, 32 }, // 24
        { 26, 12, 28, 21, 30, 29, 30, 35 }, // 25
        { 28, 12, 28, 23, 28, 34, 30, 37 }, // 26
        { 30, 12, 28, 25, 30, 34, 30, 40 }, // 27
        { 30, 13, 28, 26, 30, 35, 30, 42 }, // 28
        { 30, 14, 28, 28, 30, 38, 30, 45 }, // 29
        { 30, 15, 28, 29, 30, 40, 30, 48 }, // 30
        { 30, 16, 28, 31, 30, 43, 30, 51 }, // 31
        { 30, 17, 28, 33, 30, 45, 30, 54 }, // 32
        { 30, 18, 28, 35, 30, 48, 30, 57 }, // 33
        { 30, 19, 28, 37, 30, 51, 30, 60 }, // 34
        { 30, 19, 28, 38, 30, 53, 30, 63 }, // 35
        { 30, 20, 28, 40, 30, 56, 30, 66 }, // 36
        { 30, 21, 28, 43, 30, 59, 30, 70 }, // 37
        { 30, 22, 28, 45, 30, 62, 30, 74 }, // 38
        { 30, 24, 28, 47, 30, 65, 30, 77 }, // 39
        { 30, 25, 28, 49, 30, 68, 30, 81 }, // 40
    };

    // Per version: row/column coordinates of alignment pattern centres. Index 0 is unused.
    private static readonly byte[][] AlignmentCentres =
    {
        new byte[0],
        new byte[0],                                   // 1
        new byte[] { 6, 18 },                          // 2
        new byte[] { 6, 22 },                          // 3
        new byte[] { 6, 26 },                          // 4
        new byte[] { 6, 30 },                          // 5
        new byte[] { 6, 34 },                          // 6
        new byte[] { 6, 22, 38 },                      // 7
        new byte[] { 6, 24, 42 },                      // 8
        new byte[] { 6, 26, 46 },                      // 9
        new byte[] { 6, 28, 50 },                      // 10
        new byte[] { 6, 30, 54 },                      // 11
        new byte[] { 6, 32, 58 },                      // 12
        new byte[] { 6, 34, 62 },                      // 13
        new byte[] { 6, 26, 46, 66 },                  // 14
        new byte[] { 6, 26, 48, 70 },                  // 15
        new byte[] { 6, 26, 50, 74 },                  // 16
        new byte[] { 6, 30, 54, 78 },                  // 17
        new byte[] { 6, 30, 56, 82 },                  // 18
        new byte[] { 6, 30, 58, 86 },                  // 19
        new byte[] { 6, 34, 62, 90 },                  // 20
        new byte[] { 6, 28, 50, 72, 94 },              // 21
        new byte[] { 6, 26, 50, 74, 98 },              // 22
        new byte[] { 6, 30, 54, 78, 102 },             // 23
        new byte[] { 6, 28, 54, 80, 106 },             // 24
        new byte[] { 6, 32, 58, 84, 110 },             // 25
        new byte[] { 6, 30, 58, 86, 114 },             // 26
        new byte[] { 6, 34, 62, 90, 118 },             // 27
        new byte[] { 6, 26, 50, 74, 98, 122 },         // 28
        new byte[] { 6, 30, 54, 78, 102, 126 },        // 29
        new byte[] { 6, 26, 52, 78, 104, 130 },        // 30
        new byte[] { 6, 30, 56, 82, 108, 134 },        // 31
        new byte[] { 6, 34, 60, 86, 112, 138 },        // 32
        new byte[] { 6, 30, 58, 86, 114, 142 },        // 33
        new byte[] { 6, 34, 62, 90, 118, 146 },        // 34
        new byte[] { 6, 30, 54, 78, 102, 126, 150 },   // 35
        new byte[] { 6, 24, 50, 76, 102, 128, 154 },   // 36
        new byte[] { 6, 28, 54, 80, 106, 132, 158 },   // 37
        new byte[] { 6, 32, 58, 84, 110, 136, 162 },   // 38
        new byte[] { 6, 26, 54, 82, 110, 138, 166 },   // 39
        new byte[] { 6, 30, 58, 86, 114, 142, 170 },   // 40
    };
}

/// <summary>Error correction level; higher levels survive more damage but hold less data.</summary>
public enum Ecc
{
    /// <summary>Level L: roughly 7% of codewords can be restored.</summary>
    Low = 0,
    /// <summary>Level M: roughly 15% of codewords can be restored.</summary>
    Medium = 1,
    /// <summary>Level Q: roughly 25% of codewords can be restored.</summary>
    Quartile = 2,
    /// <summary>Level H: roughly 30% of codewords can be restored.</summary>
    High = 3,
}
