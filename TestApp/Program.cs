using System.Diagnostics;
using System.Text;
using Inspired.Codes.Graphics.QrCodeGenerator;

// Usage:
//   TestApp [text...]                print a QR code to the terminal
//   TestApp --files <dir> [text...]  write qr.svg and qr.png
//   TestApp --bench                  measure encode / render throughput
//   TestApp -- <text...>             encode text literally, even if it starts with "--"
const string DefaultText = "Hello, world!";
static string TextFrom(IEnumerable<string> words)
{
    var list = words.ToList();
    return list.Count > 0 ? string.Join(' ', list) : DefaultText;
}

switch (args.FirstOrDefault())
{
    case "--bench":
        Bench();
        break;
    case "--files" when args.Length >= 2:
        WriteFiles(args[1], TextFrom(args.Skip(2).SkipWhile((a, i) => i == 0 && a == "--")));
        break;
    case "--files":
        Console.Error.WriteLine("Usage: TestApp --files <dir> [text...]");
        Environment.ExitCode = 2;
        break;
    case "--":
        Demo(TextFrom(args.Skip(1)));
        break;
    default:
        Demo(TextFrom(args));
        break;
}

static void Demo(string text)
{
    Console.OutputEncoding = Encoding.UTF8;
    var qr = QrCode.EncodeText(text, Ecc.Medium);
    Console.WriteLine($"Version {qr.Version}, size {qr.Size}, ECC {qr.ErrorCorrectionLevel}, mask {qr.Mask}");
    Console.Write(qr.ToText());
}

static void WriteFiles(string dir, string text)
{
    Directory.CreateDirectory(dir);
    var qr = QrCode.EncodeText(text, Ecc.Medium);
    File.WriteAllText(Path.Combine(dir, "qr.svg"), qr.ToSvgString());
    File.WriteAllBytes(Path.Combine(dir, "qr.png"), qr.ToPng(scale: 8, border: 4));
    Console.WriteLine($"Wrote qr.svg and qr.png (version {qr.Version}) to {Path.GetFullPath(dir)}");
}

static void Bench()
{
    var cases = new (string Name, Func<object> Run)[]
    {
        ("EncodeText short URL (v2-3)", () => QrCode.EncodeText("https://example.com/", Ecc.Medium)),
        ("EncodeText 100 chars (v5-7)", () => QrCode.EncodeText(new string('x', 100), Ecc.Medium)),
        ("EncodeText 1000 chars (v~22)", () => QrCode.EncodeText(new string('x', 1000), Ecc.Medium)),
        ("EncodeBinary 2953 B (v40-L)", () => QrCode.EncodeBinary(new byte[2953], Ecc.Low)),
    };
    var url = QrCode.EncodeText("https://example.com/", Ecc.Medium);
    var render = new (string Name, Func<object> Run)[]
    {
        ("ToSvgString (short URL)", () => url.ToSvgString()),
        ("ToPng scale 8 (short URL)", () => url.ToPng()),
    };

    // Warm up every case first so all code paths are fully JIT-optimized (tiered compilation)
    var all = cases.Concat(render).ToArray();
    var warmTimes = new int[all.Length];
    for (int round = 0; round < 2; round++)
        for (int c = 0; c < all.Length; c++)
        {
            var w = Stopwatch.StartNew();
            int count = 0;
            while (w.ElapsedMilliseconds < 250) { all[c].Run(); count++; }
            warmTimes[c] = count;
        }

    Console.WriteLine($"{"Case",-32}{"mean",12}{"ops/s",14}{"alloc/op",12}");
    for (int c = 0; c < all.Length; c++)
    {
        var (name, run) = all[c];
        int iters = Math.Max(10, warmTimes[c] * 4); // roughly 1 s per case
        var sw = new Stopwatch();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int i = 0; i < iters; i++) run();
        sw.Stop();
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / iters;

        double us = sw.Elapsed.TotalMicroseconds / iters;
        Console.WriteLine($"{name,-32}{us,9:F1} µs{1e6 / us,14:N0}{alloc / 1024.0,9:F1} KB");
    }
}
