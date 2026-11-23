using System;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// Growable bit string, stored most significant bit first in a byte array.
/// Used to assemble segment payloads and the final data codewords.
/// </summary>
public sealed class BitBuffer
{
    private byte[] _bytes;
    private int _bitLength;

    /// <summary>Creates an empty buffer.</summary>
    public BitBuffer() : this(64) { }

    /// <summary>Creates an empty buffer pre-sized for <paramref name="capacityBits"/> bits.</summary>
    public BitBuffer(int capacityBits)
    {
        if (capacityBits < 0) throw new ArgumentOutOfRangeException(nameof(capacityBits));
        _bytes = new byte[Math.Max(1, (int)(((long)capacityBits + 7) >> 3))];
    }

    /// <summary>Number of bits written so far.</summary>
    public int BitLength => _bitLength;

    /// <summary>The backing bytes (big-endian bit order); bits at and beyond <see cref="BitLength"/> are zero.</summary>
    internal byte[] RawBytes => _bytes;

    /// <summary>The bit (0 or 1) at <paramref name="index"/>, counting from the first bit appended.</summary>
    public int GetBit(int index)
    {
        if ((uint)index >= (uint)_bitLength) throw new ArgumentOutOfRangeException(nameof(index));
        return (_bytes[index >> 3] >> (7 - (index & 7))) & 1;
    }

    /// <summary>
    /// Appends the lowest <paramref name="len"/> bits of <paramref name="val"/>, most significant first.
    /// <paramref name="len"/> must be 0 to 31 and <paramref name="val"/> must fit in that many bits.
    /// </summary>
    public void AppendBits(int val, int len)
    {
        if (len < 0 || len > 31 || val >>> len != 0)
            throw new ArgumentOutOfRangeException(nameof(len), "Value out of range");
        EnsureCapacity(len);
        while (len > 0)
        {
            int free = 8 - (_bitLength & 7);
            int n = free < len ? free : len;
            int chunk = (val >>> (len - n)) & ((1 << n) - 1);
            _bytes[_bitLength >> 3] |= (byte)(chunk << (free - n));
            _bitLength += n;
            len -= n;
        }
    }

    /// <summary>Appends whole bytes (8 bits each, most significant bit first).</summary>
    internal void AppendBytes(ReadOnlySpan<byte> data)
    {
        EnsureCapacity((long)data.Length * 8);
        int shift = _bitLength & 7;
        int pos = _bitLength >> 3;
        if (shift == 0)
        {
            data.CopyTo(_bytes.AsSpan(pos));
        }
        else
        {
            byte[] dst = _bytes;
            int back = 8 - shift;
            foreach (byte b in data)
            {
                dst[pos] |= (byte)(b >> shift);
                dst[++pos] = (byte)(b << back);
            }
        }
        _bitLength += data.Length * 8;
    }

    /// <summary>Appends all bits of another buffer.</summary>
    public void AppendData(BitBuffer bb)
    {
        if (bb == null) throw new ArgumentNullException(nameof(bb));
        int len = bb._bitLength; // capture first: bb may be this
        int full = len >> 3;
        int rem = len & 7;
        AppendBytes(new ReadOnlySpan<byte>(bb._bytes, 0, full));
        if (rem != 0)
            AppendBits(bb._bytes[full] >> (8 - rem), rem);
    }

    /// <summary>An independent copy.</summary>
    public BitBuffer Clone()
    {
        var result = new BitBuffer(_bitLength);
        Buffer.BlockCopy(_bytes, 0, result._bytes, 0, (int)(((long)_bitLength + 7) >> 3));
        result._bitLength = _bitLength;
        return result;
    }

    private void EnsureCapacity(long additionalBits)
    {
        long neededBits = _bitLength + additionalBits;
        if (neededBits > int.MaxValue) throw new InvalidOperationException("Maximum length reached");
        // +1 spare byte so unaligned byte appends may always touch the following byte.
        int neededBytes = (int)((neededBits + 7) >> 3) + 1;
        if (neededBytes > _bytes.Length)
            Array.Resize(ref _bytes, (int)Math.Min(MaxArrayLength, Math.Max(neededBytes, _bytes.Length * 2L)));
    }

    private const int MaxArrayLength = 0x7FFFFFC7; // Array.MaxLength (not available on netstandard)
}
