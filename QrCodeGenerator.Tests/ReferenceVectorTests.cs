using System.Security.Cryptography;
using System.Text.Json;
using Inspired.Codes.Graphics.QrCodeGenerator;
using Xunit;

namespace QrCodeGenerator.Tests;

/// <summary>
/// Compares output bit for bit (SHA-256 of the packed module grid) with vectors produced by an
/// independent QR Code encoder, including the automatically selected mask.
/// </summary>
public class ReferenceVectorTests
{
    public sealed record Vector(
        string? Text, string? Bytes, int Ecc, int MinVersion, int MaxVersion, int Mask, bool Boost,
        int ExpVersion, int ExpEcc, int ExpMask, string Sha256);

    private static readonly Vector[] Vectors = JsonSerializer.Deserialize<Vector[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference-vectors.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static IEnumerable<object[]> Cases() => Enumerable.Range(0, Vectors.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceImplementation(int index)
    {
        Vector v = Vectors[index];
        IReadOnlyList<QrSegment> segs = v.Text != null
            ? QrSegment.MakeSegments(v.Text)
            : new[] { QrSegment.MakeBytes(Convert.FromBase64String(v.Bytes!)) };

        QrCode qr = QrCode.EncodeSegments(segs, (Ecc)v.Ecc, v.MinVersion, v.MaxVersion, v.Mask, v.Boost);

        Assert.Equal(v.ExpVersion, qr.Version);
        Assert.Equal(v.ExpVersion * 4 + 17, qr.Size);
        Assert.Equal((Ecc)v.ExpEcc, qr.ErrorCorrectionLevel);
        Assert.Equal(v.ExpMask, qr.Mask);
        Assert.Equal(v.Sha256, HashModules(qr));
    }

    [Fact]
    public void CoversAllVersionsAndMasks()
    {
        Assert.Equal(Enumerable.Range(1, 40), Vectors.Select(v => v.ExpVersion).Distinct().Order());
        Assert.Equal(Enumerable.Range(0, 8), Vectors.Select(v => v.ExpMask).Distinct().Order());
        Assert.Equal(Enumerable.Range(0, 4), Vectors.Select(v => v.ExpEcc).Distinct().Order());
    }

    internal static string HashModules(QrCode qr)
    {
        int n = qr.Size;
        var bits = new byte[(n * n + 7) / 8];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (qr.GetModule(x, y))
                {
                    int i = y * n + x;
                    bits[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
        return Convert.ToHexStringLower(SHA256.HashData(bits));
    }
}
