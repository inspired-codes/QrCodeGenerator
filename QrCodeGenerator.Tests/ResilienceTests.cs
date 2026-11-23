using System.Diagnostics;
using Inspired.Codes.Graphics.QrCodeGenerator;
using Xunit;

namespace QrCodeGenerator.Tests;

/// <summary>Extreme and hostile arguments must fail fast with the documented exception types.</summary>
public class ResilienceTests
{
    [Fact]
    public void Png_RejectsImagesLargerThanTheLimitWithoutAllocating()
    {
        var qr = QrCode.EncodeBinary(new byte[2953], Ecc.Low); // version 40, 177 modules
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => qr.ToPng(scale: 256, border: 1024));
        Assert.Contains(QrCode.MaxPngDimension.ToString(), ex.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, "should fail before allocating the image");
        Assert.Throws<ArgumentOutOfRangeException>(() => qr.WritePng(Stream.Null, scale: 256, border: 4));
    }

    [Fact]
    public void Png_LimitIsInclusive()
    {
        var qr = QrCode.EncodeText("x", Ecc.Low); // version 1, 21 modules
        // (21 + 2 * 53) * 256 = 32512 <= 32768 is allowed; border 54 gives 33024, rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => qr.WritePng(Stream.Null, scale: 256, border: 54));
        var counting = new CountingStream();
        qr.WritePng(counting, scale: 256, border: 53);
        Assert.True(counting.Length > 0);
    }

    [Fact]
    public void Png_StreamsScanlinesInsteadOfBufferingTheBitmap()
    {
        var qr = QrCode.EncodeText("x", Ecc.Low);
        // 15104 x 15104 pixels = ~28 MB uncompressed; streaming should allocate far less.
        long before = GC.GetAllocatedBytesForCurrentThread();
        qr.WritePng(Stream.Null, scale: 256, border: 19);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 << 20, $"allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(2954)]
    [InlineData(1 << 28)] // bit length (length * 8) overflows int
    public void HugeBinary_ThrowsDataTooLong(int length)
    {
        byte[] data = new byte[length];
        Assert.Throws<DataTooLongException>(() => QrSegment.MakeBytes(data));
        Assert.Throws<DataTooLongException>(() => QrCode.EncodeBinary(data, Ecc.Low));
    }

    [Fact]
    public void HugeText_ThrowsDataTooLongFast()
    {
        string huge = new string('1', 300_000_000); // bit length estimate overflows int
        var sw = Stopwatch.StartNew();
        Assert.Throws<DataTooLongException>(() => QrSegment.MakeNumeric(huge));
        Assert.Throws<DataTooLongException>(() => QrSegment.MakeSegments(huge));
        Assert.Throws<DataTooLongException>(() => QrCode.EncodeText(huge, Ecc.Low));
        Assert.True(sw.ElapsedMilliseconds < 5000);

        Assert.Throws<DataTooLongException>(() => QrSegment.MakeNumeric(new string('1', 7090)));
        Assert.Throws<DataTooLongException>(() => QrSegment.MakeAlphanumeric(new string('A', 4297)));
        Assert.Throws<DataTooLongException>(() => QrCode.EncodeText(new string('a', 2954), Ecc.Low));
    }

    [Fact]
    public void MaximumPayloadsStillEncode()
    {
        Assert.Equal(40, QrCode.EncodeText(new string('1', 7089), Ecc.Low).Version);
        Assert.Equal(40, QrCode.EncodeText(new string('A', 4296), Ecc.Low).Version);
        Assert.Equal(40, QrCode.EncodeBinary(new byte[2953], Ecc.Low).Version);
    }

    [Fact]
    public void BitBuffer_HandlesLargeCapacityHints()
    {
        var bb = new BitBuffer(1 << 30);
        bb.AppendBits(5, 3);
        Assert.Equal(3, bb.BitLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitBuffer(-1));
    }

    [Fact]
    public void ReedSolomonTables_AreSafeToBuildConcurrently()
    {
        // Each run starts many threads that race to encode every ECC block size at once.
        var payloads = Enumerable.Range(1, 40)
            .SelectMany(v => Enum.GetValues<Ecc>().Select(e => (v, e))).ToArray();
        var expected = payloads.ToDictionary(p => p,
            p => ReferenceVectorTests.HashModules(QrCode.EncodeSegments(QrSegment.MakeSegments("rs"), p.e, p.v, p.v)));
        Parallel.ForEach(payloads.Concat(payloads).Concat(payloads), p =>
            Assert.Equal(expected[p], ReferenceVectorTests.HashModules(QrCode.EncodeSegments(QrSegment.MakeSegments("rs"), p.e, p.v, p.v))));
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
    }
}
