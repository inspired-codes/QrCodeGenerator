using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

public sealed partial class QrCode
{
    /// <summary>
    /// Renders the symbol as a standalone SVG document, one unit per module. All dark modules form a
    /// single path in which horizontal runs are merged, which keeps the markup compact.
    /// </summary>
    /// <param name="border">Quiet zone width in modules on each side; scanners expect at least 4.</param>
    /// <param name="darkColor">CSS color for dark modules.</param>
    /// <param name="lightColor">CSS color for the background.</param>
    public string ToSvgString(int border = 4, string darkColor = "#000000", string lightColor = "#FFFFFF")
    {
        if (border < 0 || border > 1024) throw new ArgumentOutOfRangeException(nameof(border));
        if (darkColor == null) throw new ArgumentNullException(nameof(darkColor));
        if (lightColor == null) throw new ArgumentNullException(nameof(lightColor));

        var path = new StringBuilder(Size * Size);
        for (int y = 0; y < Size; y++)
        {
            int row = y * Size;
            int x = 0;
            while (x < Size)
            {
                if (_modules[row + x] == 0) { x++; continue; }
                int start = x;
                while (x < Size && _modules[row + x] != 0) x++;
                if (path.Length > 0) path.Append(' ');
                int width = x - start;
                path.Append('M').Append(start + border).Append(' ').Append(y + border)
                    .Append('h').Append(width).Append("v1h-").Append(width).Append('z');
            }
        }

        int extent = Size + 2 * border;
        var svg = new StringBuilder(path.Length + 320);
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
           .Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(extent)
           .Append("\" height=\"").Append(extent).Append("\" viewBox=\"0 0 ").Append(extent).Append(' ').Append(extent)
           .Append("\" shape-rendering=\"crispEdges\">\n")
           .Append("  <rect width=\"").Append(extent).Append("\" height=\"").Append(extent)
           .Append("\" fill=\"").Append(EscapeXml(lightColor)).Append("\"/>\n")
           .Append("  <path fill=\"").Append(EscapeXml(darkColor)).Append("\" d=\"").Append(path).Append("\"/>\n")
           .Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>Largest PNG width/height in pixels accepted by <see cref="ToPng"/> and <see cref="WritePng"/>.</summary>
    public const int MaxPngDimension = 32768;

    /// <summary>
    /// Returns this QR Code as a black and white PNG image (1 bit per pixel).
    /// </summary>
    /// <param name="scale">Pixels per module (1 to 256).</param>
    /// <param name="border">Number of light border modules (quiet zone) on each side; the standard asks for 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the image would be wider than <see cref="MaxPngDimension"/>.</exception>
    public byte[] ToPng(int scale = 8, int border = 4)
    {
        using var ms = new MemoryStream();
        WritePng(ms, scale, border);
        return ms.ToArray();
    }

    /// <summary>Writes this QR Code as a black and white PNG image (1 bit per pixel) to the stream.</summary>
    /// <param name="output">The stream to write to.</param>
    /// <param name="scale">Pixels per module (1 to 256).</param>
    /// <param name="border">Number of light border modules (quiet zone) on each side; the standard asks for 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the image would be wider than <see cref="MaxPngDimension"/>.</exception>
    public void WritePng(Stream output, int scale = 8, int border = 4)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (scale < 1 || scale > 256) throw new ArgumentOutOfRangeException(nameof(scale));
        if (border < 0 || border > 1024) throw new ArgumentOutOfRangeException(nameof(border));
        long dimLong = (Size + 2L * border) * scale;
        if (dimLong > MaxPngDimension)
            throw new ArgumentOutOfRangeException(nameof(scale),
                $"Image would be {dimLong} pixels wide; the maximum is {MaxPngDimension}. Use a smaller scale or border.");

        int dim = (int)dimLong;
        int stride = (dim + 7) / 8 + 1; // filter byte + packed pixels

        // Scanlines: filter type 0, bit 1 = white, 0 = black. Each module row is built once and
        // streamed 'scale' times into the compressor, so memory stays at one scanline.
        // A band holds several copies of the current scanline, so the compressor gets few, larger writes.
        int linesPerBand = Math.Max(1, Math.Min(scale, 65536 / stride));
        byte[] band = new byte[stride * linesPerBand];
        var line = new Span<byte>(band, 0, stride);
        using var zlib = new MemoryStream();
        zlib.WriteByte(0x78); // CMF: deflate, 32K window
        zlib.WriteByte(0x01); // FLG: fastest, check bits
        uint adlerA = 1, adlerB = 0;
        using (var deflate = new DeflateStream(zlib, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int my = -border; my < Size + border; my++)
            {
                line.Slice(1).Fill(0xFF);
                if ((uint)my < (uint)Size)
                {
                    int row = my * Size;
                    for (int mx = 0; mx < Size; mx++)
                    {
                        if (_modules[row + mx] == 0) continue;
                        int px = (mx + border) * scale;
                        for (int p = px; p < px + scale; p++)
                            line[1 + (p >> 3)] &= (byte)~(0x80 >> (p & 7));
                    }
                }
                if ((dim & 7) != 0) // clear unused trailing bits
                    line[stride - 1] &= (byte)(0xFF << (8 - (dim & 7)));

                for (int r = 1; r < linesPerBand; r++)
                    line.CopyTo(new Span<byte>(band, r * stride, stride));
                for (int remaining = scale; remaining > 0; remaining -= linesPerBand)
                {
                    int bytes = Math.Min(remaining, linesPerBand) * stride;
                    deflate.Write(band, 0, bytes);
                    Adler32(ref adlerA, ref adlerB, new ReadOnlySpan<byte>(band, 0, bytes));
                }
            }
        }
        uint adler = (adlerB << 16) | adlerA;
        zlib.WriteByte((byte)(adler >> 24));
        zlib.WriteByte((byte)(adler >> 16));
        zlib.WriteByte((byte)(adler >> 8));
        zlib.WriteByte((byte)adler);

        Span<byte> header = stackalloc byte[13];
        WriteBE(header, 0, dim);
        WriteBE(header, 4, dim);
        header[8] = 1;  // bit depth
        header[9] = 0;  // grayscale
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace

        ReadOnlySpan<byte> signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        output.Write(signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", new ReadOnlySpan<byte>(zlib.GetBuffer(), 0, (int)zlib.Length));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Returns a text rendering for terminals using Unicode half blocks (two module rows per text line).
    /// Dark modules are drawn as blank, so it scans on a dark-background terminal; pass invert = true for light backgrounds.
    /// </summary>
    public string ToText(int border = 2, bool invert = false)
    {
        if (border < 0 || border > 1024) throw new ArgumentOutOfRangeException(nameof(border));
        int dim = Size + border * 2;
        var sb = new StringBuilder((dim + 1) * ((dim + 1) / 2));
        for (int y = -border; y < Size + border; y += 2)
        {
            for (int x = -border; x < Size + border; x++)
            {
                // "lit" = character cell painted in foreground color
                bool top = GetModule(x, y) == invert;
                bool bottom = y + 1 < Size + border && GetModule(x, y + 1) == invert;
                sb.Append(top ? (bottom ? '█' : '▀') : (bottom ? '▄' : ' '));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /*---- PNG helpers ----*/

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[8];
        WriteBE(buf, 0, data.Length);
        for (int i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
        output.Write(buf);
        output.Write(data);
        uint crc = Crc32(0xFFFFFFFFu, buf.Slice(4, 4));
        crc = Crc32(crc, data) ^ 0xFFFFFFFFu;
        WriteBE(buf, 0, (int)crc);
        output.Write(buf.Slice(0, 4));
    }

    // Running Adler-32 (zlib checksum) over the uncompressed data.
    private static void Adler32(ref uint a, ref uint b, ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length)
        {
            int end = Math.Min(data.Length, i + 5552); // largest block before the sums can overflow
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }
            a %= 65521;
            b %= 65521;
        }
    }

    private static void WriteBE(Span<byte> buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static string EscapeXml(string s)
    {
        if (s.IndexOfAny(XmlSpecial) < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    private static readonly char[] XmlSpecial = { '&', '<', '>', '"', '\'' };
}
