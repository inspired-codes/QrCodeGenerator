using System.IO.Compression;
using System.Text;
using Inspired.Codes.Graphics.QrCodeGenerator;
using Xunit;

namespace QrCodeGenerator.Tests;

public class ApiTests
{
    [Fact]
    public void BitBuffer_AppendsAndReadsBack()
    {
        var bb = new BitBuffer();
        bb.AppendBits(0b101, 3);
        bb.AppendBits(0, 0);
        bb.AppendBits(0x7FFFFFFF, 31);
        var other = new BitBuffer();
        other.AppendBits(0b0110, 4);
        bb.AppendData(other);
        bb.AppendData(bb.Clone());

        Assert.Equal((3 + 31 + 4) * 2, bb.BitLength);
        string expected = "101" + new string('1', 31) + "0110";
        string actual = string.Concat(Enumerable.Range(0, bb.BitLength).Select(i => bb.GetBit(i)));
        Assert.Equal(expected + expected, actual);
        Assert.Throws<ArgumentOutOfRangeException>(() => bb.AppendBits(4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => bb.GetBit(bb.BitLength));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0123456789", 1)]
    [InlineData("HELLO WORLD", 2)]
    [InlineData("Hello world", 4)]
    public void MakeSegments_PicksMostCompactMode(string text, int modeBits)
    {
        var segs = QrSegment.MakeSegments(text);
        if (text.Length == 0) { Assert.Empty(segs); return; }
        Assert.Single(segs);
        Assert.Equal(modeBits, segs[0].Mode.ModeBits);
        Assert.Equal(text.Length, segs[0].NumChars);
    }

    [Fact]
    public void Segment_IsImmutable()
    {
        var seg = QrSegment.MakeNumeric("123");
        seg.GetData().AppendBits(1, 1);
        Assert.Equal(10, seg.GetData().BitLength);
    }

    [Fact]
    public void Validation()
    {
        Assert.Throws<ArgumentException>(() => QrSegment.MakeNumeric("12a"));
        Assert.Throws<ArgumentException>(() => QrSegment.MakeAlphanumeric("abc"));
        Assert.Throws<ArgumentNullException>(() => QrCode.EncodeText(null!, Ecc.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.EncodeText("x", (Ecc)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.EncodeSegments(QrSegment.MakeSegments("x"), Ecc.Low, mask: 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.EncodeSegments(QrSegment.MakeSegments("x"), Ecc.Low, 5, 4));
        Assert.Throws<DataTooLongException>(() => QrCode.EncodeBinary(new byte[2954], Ecc.Low));
        Assert.Throws<DataTooLongException>(() => QrCode.EncodeSegments(QrSegment.MakeSegments(new string('a', 100)), Ecc.Low, 1, 3));
    }

    [Fact]
    public void GetModule_OutOfBoundsIsLight()
    {
        var qr = QrCode.EncodeText("x", Ecc.Low);
        Assert.False(qr.GetModule(-1, 0));
        Assert.False(qr.GetModule(0, qr.Size));
        Assert.True(qr.GetModule(0, 0)); // finder corner
    }

    [Fact]
    public void EccSegmentsAndEci_Roundtrip()
    {
        var segs = new[] { QrSegment.MakeEci(26), QrSegment.MakeBytes(Encoding.UTF8.GetBytes("€")) };
        var qr = QrCode.EncodeSegments(segs, Ecc.Medium);
        Assert.Equal(1, qr.Version);
    }

    [Fact]
    public void IsThreadSafe()
    {
        string[] inputs = Enumerable.Range(0, 64).Select(i => new string('x', i * 20)).ToArray();
        var expected = inputs.Select(s => ReferenceVectorTests.HashModules(QrCode.EncodeText(s, Ecc.Medium))).ToArray();
        Parallel.For(0, 2000, i =>
        {
            int k = i % inputs.Length;
            Assert.Equal(expected[k], ReferenceVectorTests.HashModules(QrCode.EncodeText(inputs[k], Ecc.Medium)));
        });
    }

    [Fact]
    public void Svg_HasOnePathAndCorrectViewBox()
    {
        var qr = QrCode.EncodeText("Hello, world!", Ecc.Medium);
        string svg = qr.ToSvgString(4, "#123", "<x>");
        Assert.Contains($"viewBox=\"0 0 {qr.Size + 8} {qr.Size + 8}\"", svg);
        Assert.Contains("fill=\"&lt;x&gt;\"", svg);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "<path"));

        // Every dark module is covered by exactly one run, light modules by none
        var covered = new bool[qr.Size, qr.Size];
        string d = System.Text.RegularExpressions.Regex.Match(svg, "d=\"([^\"]*)\"").Groups[1].Value;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(d, @"M(\d+) (\d+)h(\d+)v1h-\3z"))
        {
            int x0 = int.Parse(m.Groups[1].Value) - 4, y = int.Parse(m.Groups[2].Value) - 4, len = int.Parse(m.Groups[3].Value);
            for (int x = x0; x < x0 + len; x++) { Assert.False(covered[y, x]); covered[y, x] = true; }
        }
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                Assert.Equal(qr.GetModule(x, y), covered[y, x]);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 4)]
    [InlineData(8, 4)]
    public void Png_DecodesToModules(int scale, int border)
    {
        var qr = QrCode.EncodeText("PNG check 123", Ecc.Quartile);
        byte[] png = qr.ToPng(scale, border);
        int dim = (qr.Size + 2 * border) * scale;

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(dim, ReadBE(png, 16));
        Assert.Equal(dim, ReadBE(png, 20));

        // Collect IDAT, check CRCs, inflate
        var idat = new MemoryStream();
        for (int pos = 8; pos < png.Length; )
        {
            int len = ReadBE(png, pos);
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            uint crc = Crc32(png.AsSpan(pos + 4, len + 4));
            Assert.Equal(crc, (uint)ReadBE(png, pos + 8 + len));
            if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        using var z = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] px = raw.ToArray();
        int stride = (dim + 7) / 8 + 1;
        Assert.Equal(stride * dim, px.Length);

        for (int y = 0; y < dim; y++)
        {
            Assert.Equal(0, px[y * stride]);
            for (int x = 0; x < dim; x++)
            {
                bool white = (px[y * stride + 1 + x / 8] & (0x80 >> (x % 8))) != 0;
                Assert.Equal(!qr.GetModule(x / scale - border, y / scale - border), white);
            }
        }
    }

    [Fact]
    public void Text_HasExpectedShape()
    {
        var qr = QrCode.EncodeText("hi", Ecc.Low);
        string[] lines = qr.ToText(border: 2).TrimEnd('\n').Split('\n');
        Assert.Equal((qr.Size + 4 + 1) / 2, lines.Length);
        Assert.All(lines, l => Assert.Equal(qr.Size + 4, l.Length));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    private static int ReadBE(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];
}
