using System;
using System.Collections.Generic;
using System.Text;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// One run of payload in a single encoding mode (numeric, alphanumeric, byte, ...), ready to be
/// placed in a symbol. Several segments can be combined with <see cref="QrCode.EncodeSegments"/>.
/// <para>Use the <c>Make*</c> factories for ordinary payloads; the public constructor accepts
/// hand-built bits for anything else. Segments are immutable.</para>
/// <para>The factories throw <see cref="DataTooLongException"/> for payloads no symbol can hold
/// (more than 7089 digits, 4296 alphanumeric characters or 2953 bytes).</para>
/// </summary>
public sealed class QrSegment
{
    /// <summary>Byte-mode segment holding the given bytes (8 bits each).</summary>
    /// <exception cref="DataTooLongException">The payload is longer than any QR Code can hold.</exception>
    public static QrSegment MakeBytes(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return MakeBytes(new ReadOnlySpan<byte>(data));
    }

    /// <summary>Byte-mode segment holding the given bytes (8 bits each).</summary>
    /// <exception cref="DataTooLongException">The payload is longer than any QR Code can hold.</exception>
    public static QrSegment MakeBytes(ReadOnlySpan<byte> data)
    {
        CheckLength(data.Length, MaxBytes, "bytes");
        var bb = new BitBuffer(data.Length * 8);
        bb.AppendBytes(data);
        return new QrSegment(Mode.Byte, data.Length, bb, clone: false);
    }

    /// <summary>Numeric-mode segment for a string of ASCII digits (about 3.3 bits per digit).</summary>
    /// <exception cref="DataTooLongException">The payload is longer than any QR Code can hold.</exception>
    public static QrSegment MakeNumeric(string digits)
    {
        if (digits == null) throw new ArgumentNullException(nameof(digits));
        if (!IsNumeric(digits))
            throw new ArgumentException("String contains non-numeric characters", nameof(digits));
        CheckLength(digits.Length, MaxDigits, "digits");

        var bb = new BitBuffer(digits.Length * 3 + digits.Length / 3 + 4);
        int i = 0;
        for (; i + 3 <= digits.Length; i += 3)
            bb.AppendBits((digits[i] - '0') * 100 + (digits[i + 1] - '0') * 10 + (digits[i + 2] - '0'), 10);
        int rest = digits.Length - i;
        if (rest == 2)
            bb.AppendBits((digits[i] - '0') * 10 + (digits[i + 1] - '0'), 7);
        else if (rest == 1)
            bb.AppendBits(digits[i] - '0', 4);
        return new QrSegment(Mode.Numeric, digits.Length, bb, clone: false);
    }

    /// <summary>
    /// Alphanumeric-mode segment (5.5 bits per character) for text made only of digits,
    /// upper-case letters A-Z and the nine symbols space $ % * + - . / :
    /// </summary>
    /// <exception cref="DataTooLongException">The payload is longer than any QR Code can hold.</exception>
    public static QrSegment MakeAlphanumeric(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (!IsAlphanumeric(text))
            throw new ArgumentException("String contains unencodable characters in alphanumeric mode", nameof(text));
        CheckLength(text.Length, MaxAlphanumeric, "alphanumeric characters");

        var bb = new BitBuffer(text.Length * 6);
        int i = 0;
        for (; i + 2 <= text.Length; i += 2)
            bb.AppendBits(AlphanumericMap[text[i]] * 45 + AlphanumericMap[text[i + 1]], 11);
        if (i < text.Length)
            bb.AppendBits(AlphanumericMap[text[i]], 6);
        return new QrSegment(Mode.Alphanumeric, text.Length, bb, clone: false);
    }

    /// <summary>
    /// Segments for arbitrary text: a single segment in the densest mode that covers every character
    /// (numeric, else alphanumeric, else UTF-8 bytes), or an empty list for empty text.
    /// </summary>
    /// <exception cref="DataTooLongException">The payload is longer than any QR Code can hold.</exception>
    public static List<QrSegment> MakeSegments(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        // No mode can hold more characters than numeric mode; fail before doing any work.
        CheckLength(text.Length, MaxDigits, "characters");
        var result = new List<QrSegment>(1);
        if (text.Length == 0) { }
        else if (IsNumeric(text)) result.Add(MakeNumeric(text));
        else if (IsAlphanumeric(text)) result.Add(MakeAlphanumeric(text));
        else result.Add(MakeBytes(Encoding.UTF8.GetBytes(text)));
        return result;
    }

    /// <summary>
    /// ECI designator segment that tells readers how to interpret the following bytes,
    /// e.g. 26 for UTF-8. Values 0 to 999999 are encoded in 1, 2 or 3 bytes.
    /// </summary>
    public static QrSegment MakeEci(int assignVal)
    {
        var bb = new BitBuffer(24);
        if (assignVal < 0)
            throw new ArgumentOutOfRangeException(nameof(assignVal), "ECI assignment value out of range");
        else if (assignVal < (1 << 7))
            bb.AppendBits(assignVal, 8);
        else if (assignVal < (1 << 14))
        {
            bb.AppendBits(2, 2);
            bb.AppendBits(assignVal, 14);
        }
        else if (assignVal < 1000000)
        {
            bb.AppendBits(6, 3);
            bb.AppendBits(assignVal, 21);
        }
        else
            throw new ArgumentOutOfRangeException(nameof(assignVal), "ECI assignment value out of range");
        return new QrSegment(Mode.Eci, 0, bb, clone: false);
    }

    /// <summary>True if the text consists only of the digits 0-9 (and so fits numeric mode).</summary>
    public static bool IsNumeric(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        foreach (char c in text)
            if ((uint)(c - '0') > 9) return false;
        return true;
    }

    /// <summary>True if every character of the text is in the 45-character alphanumeric set.</summary>
    public static bool IsAlphanumeric(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        foreach (char c in text)
            if (c >= 128 || AlphanumericMap[c] < 0) return false;
        return true;
    }

    /// <summary>Encoding mode of the payload.</summary>
    public Mode Mode { get; }

    /// <summary>
    /// Value written to the character count field: characters for numeric, alphanumeric and kanji
    /// segments, bytes for byte segments, 0 for ECI designators.
    /// </summary>
    public int NumChars { get; }

    // Never exposed directly, so it stays immutable.
    internal BitBuffer Data { get; }

    /// <summary>
    /// Wraps hand-built payload bits. The caller is responsible for <paramref name="numChars"/> matching
    /// the bits for the given mode; this is not verified. The bits are copied.
    /// </summary>
    public QrSegment(Mode mode, int numChars, BitBuffer data)
        : this(mode, numChars, data ?? throw new ArgumentNullException(nameof(data)), clone: true) { }

    private QrSegment(Mode mode, int numChars, BitBuffer data, bool clone)
    {
        Mode = mode ?? throw new ArgumentNullException(nameof(mode));
        if (numChars < 0) throw new ArgumentOutOfRangeException(nameof(numChars), "Invalid value");
        NumChars = numChars;
        Data = clone ? data.Clone() : data;
    }

    /// <summary>A copy of the payload bits (without mode indicator and character count).</summary>
    public BitBuffer GetData() => Data.Clone();

    /// <summary>
    /// Total size in bits of the segments (mode indicators, character counts and payloads) in a symbol of
    /// the given version, or -1 if a character count overflows its field or the total exceeds int.MaxValue.
    /// </summary>
    public static int GetTotalBits(IReadOnlyList<QrSegment> segs, int version)
    {
        if (segs == null) throw new ArgumentNullException(nameof(segs));
        long result = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            QrSegment seg = segs[i] ?? throw new ArgumentNullException(nameof(segs), "Null segment");
            int ccBits = seg.Mode.NumCharCountBits(version);
            if (seg.NumChars >= (1 << ccBits))
                return -1;
            result += 4L + ccBits + seg.Data.BitLength;
            if (result > int.MaxValue)
                return -1;
        }
        return (int)result;
    }

    // Largest payloads any QR Code (version 40, ECC Low) can hold, per mode.
    private const int MaxDigits = 7089;
    private const int MaxAlphanumeric = 4296;
    private const int MaxBytes = 2953;

    // Rejects payloads that cannot fit any QR Code before allocating anything for them.
    private static void CheckLength(int length, int max, string unit)
    {
        if (length > max)
            throw new DataTooLongException($"Data length = {length} {unit}, max capacity of any QR Code = {max} {unit}");
    }

    // ASCII -> alphanumeric value (0..44), or -1 if not encodable.
    private static readonly sbyte[] AlphanumericMap = BuildAlphanumericMap();

    private static sbyte[] BuildAlphanumericMap()
    {
        const string charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        var map = new sbyte[128];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        for (int i = 0; i < charset.Length; i++) map[charset[i]] = (sbyte)i;
        return map;
    }
}

/// <summary>Segment encoding mode: its 4-bit indicator and the width of its character count field.</summary>
public sealed class Mode
{
    // Character count field widths for versions 1-9, 10-26 and 27-40.
    public static readonly Mode Numeric      = new Mode(0x1, 10, 12, 14);
    public static readonly Mode Alphanumeric = new Mode(0x2,  9, 11, 13);
    public static readonly Mode Byte         = new Mode(0x4,  8, 16, 16);
    public static readonly Mode Kanji        = new Mode(0x8,  8, 10, 12);
    public static readonly Mode Eci          = new Mode(0x7,  0,  0,  0);

    /// <summary>The 4-bit mode indicator written before the segment.</summary>
    public int ModeBits { get; }

    private readonly int _cc0, _cc1, _cc2;

    private Mode(int mode, int cc0, int cc1, int cc2)
    {
        ModeBits = mode;
        _cc0 = cc0; _cc1 = cc1; _cc2 = cc2;
    }

    /// <summary>Width in bits (0 to 16) of the character count field at the given version.</summary>
    public int NumCharCountBits(int ver) => ver <= 9 ? _cc0 : ver <= 26 ? _cc1 : _cc2;
}
