# QrCodeGenerator

A fast, lightweight, dependency-free QR Code generator for .NET.

- QR Code Model 2, all 40 versions, all 4 error correction levels
- Automatic mode selection (numeric / alphanumeric / UTF-8 byte), ECI segments, manual segments
- Automatic mask selection using the ISO/IEC 18004 penalty rules
- Output: SVG, PNG (1-bit, no imaging library needed), Unicode text, or raw module access
- No dependencies; targets `netstandard2.1` and `net8.0` (hardware popcount on modern runtimes)
- Immutable results, thread-safe

## Usage

```csharp
using Inspired.Codes.Graphics.QrCodeGenerator;

var qr = QrCode.EncodeText("https://example.com/", Ecc.Medium);

string svg = qr.ToSvgString(border: 4);           // vector, horizontal runs merged into one path
byte[] png = qr.ToPng(scale: 8, border: 4);       // black & white PNG
Console.Write(qr.ToText());                       // terminal preview with half blocks

for (int y = 0; y < qr.Size; y++)                 // or draw it yourself
    for (int x = 0; x < qr.Size; x++)
        if (qr.GetModule(x, y)) { /* dark module */ }
```

Binary data and fine control:

```csharp
var qr1 = QrCode.EncodeBinary(bytes, Ecc.Low);

var segs = new[] { QrSegment.MakeEci(26), QrSegment.MakeBytes(Encoding.UTF8.GetBytes("€ 10")) };
var qr2 = QrCode.EncodeSegments(segs, Ecc.High, minVersion: 5, maxVersion: 10, mask: -1, boostEcl: true);
```

`DataTooLongException` is thrown when the data does not fit (max 2953 bytes / 4296 alphanumeric / 7089 digits at `Ecc.Low`).
Payloads larger than any QR Code can hold are rejected up front by the segment factories, before any work is done.

`ToPng` / `WritePng` stream scanlines through the compressor (memory stays at a 64 KB band plus the compressed output)
and reject images wider than `QrCode.MaxPngDimension` (32768 px) with `ArgumentOutOfRangeException`.

## Performance

`dotnet run --project TestApp -c Release -- --bench` (.NET 10, x64; times per call, including automatic mask selection):

| Case                           |   Time | Allocated |
|--------------------------------|-------:|----------:|
| Short URL (version 2)          | 6.5 µs |    3.3 KB |
| 100 chars (version 5)          |  12 µs |    6.9 KB |
| 1000 chars (version 22)        |  92 µs |     50 KB |
| 2953 bytes (version 40)        | 145 µs |    114 KB |
| `ToSvgString` (short URL)      | 3.3 µs |     15 KB |
| `ToPng` scale 8 (short URL)    |  13 µs |    5.5 KB |

How:

- Bits are packed into bytes (`BitBuffer`); modules live in one flat `byte[]`
- Reed-Solomon uses a per-degree product table with the remainder held in four 64-bit words
- Data and ECC codewords are written straight to their interleaved positions
- Mask selection packs rows and columns into 64-bit words, applies the masks via precomputed
  periodic patterns, and scores the penalty rules with shifts and popcounts instead of per-module branches

## Testing

```
dotnet test                                    # against the net8.0 build
dotnet test -p:LibTfm=netstandard2.1           # against the netstandard2.1 build
```

- `reference-vectors.json`: 432 cases cross-checked against an independent encoder (all versions, masks,
  ECC levels, modes, max capacities). The module grid must match bit for bit, including the chosen mask.
- The bit-parallel penalty scorer is checked against a plain scalar evaluation of the same rules on random
  and adversarial grids (planted finder-like patterns of module size 1 to 6) for every symbol size.
- PNG output is decoded and compared module by module; SVG paths are checked to cover exactly the dark modules.
- Resilience: oversized images, huge payloads (including sizes whose bit length overflows `int`), maximum payloads,
  and concurrent first use of every Reed-Solomon table.
- Generated PNGs were also scanned with ZXing-C++ for all 40 versions and ECC levels.

TestApp: `TestApp [text]` prints a QR code, `TestApp --files <dir> [text]` writes `qr.svg`/`qr.png`,
`TestApp --bench` runs the benchmark, and `TestApp -- <text>` encodes text that starts with `--`.

## Version Info

1.0.0: initial release.
