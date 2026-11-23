using Inspired.Codes.Graphics.QrCodeGenerator;
using Xunit;

namespace QrCodeGenerator.Tests;

/// <summary>The bit-parallel penalty scorer must agree exactly with a plain scalar evaluation of the same rules.</summary>
public class PenaltyTests
{
    public static IEnumerable<object[]> Sizes() => Enumerable.Range(1, 40).Select(v => new object[] { v * 4 + 17 });

    [Theory]
    [MemberData(nameof(Sizes))]
    public void BitParallelScoreMatchesScalarReference(int size)
    {
        var rng = new Random(size);
        for (int trial = 0; trial < 40; trial++)
        {
            byte[] m = new byte[size * size];
            int style = trial % 4;
            for (int i = 0; i < m.Length; i++)
                m[i] = style switch
                {
                    0 => (byte)rng.Next(2),                       // uniform random
                    1 => (byte)(rng.Next(10) < 8 ? 1 : 0),        // mostly dark: long runs, big N4
                    2 => (byte)(rng.Next(10) < 2 ? 1 : 0),        // mostly light
                    _ => 0,
                };
            if (style == 3)
            {
                // Plant finder-like patterns of various module sizes (n = 1..6), rows and columns, at random offsets
                for (int k = 0; k < 30; k++)
                {
                    int n = rng.Next(1, Math.Min(7, size / 7 + 1));
                    int line = rng.Next(size);
                    int start = rng.Next(-2, size - 7 * n + 3);
                    bool vertical = rng.Next(2) == 0;
                    int[] runs = { n, n, 3 * n, n, n };
                    int pos = start, color = 1;
                    foreach (int r in runs)
                    {
                        for (int j = 0; j < r; j++, pos++)
                            if (pos >= 0 && pos < size)
                                m[vertical ? pos * size + line : line * size + pos] = (byte)color;
                        color ^= 1;
                    }
                }
            }
            Assert.Equal(ScalarPenalty(m, size), MaskEvaluator.Score(m, size));
        }
    }

    // Plain, module-by-module evaluation of the four penalty rules, written for clarity rather than speed.
    private static int ScalarPenalty(byte[] m, int size)
    {
        int total = 0;
        var line = new bool[size];
        for (int orientation = 0; orientation < 2; orientation++)
        {
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                    line[b] = (orientation == 0 ? m[a * size + b] : m[b * size + a]) != 0;
                total += RunPenalty(line) + 40 * FinderLikePatterns(line);
            }
        }

        // 3 points for every 2x2 block of one color (overlapping blocks all count)
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                byte c = m[y * size + x];
                if (m[y * size + x + 1] == c && m[(y + 1) * size + x] == c && m[(y + 1) * size + x + 1] == c)
                    total += 3;
            }

        // 10 points per started 5% step of dark share deviation beyond the first 5% from 50%
        double darkPercent = 100.0 * m.Count(v => v != 0) / m.Length;
        int steps = (int)Math.Ceiling(Math.Abs(darkPercent - 50) / 5) - 1;
        return total + 10 * Math.Max(0, steps);
    }

    // 3 points for a run of 5 same-colored modules, plus 1 for every module beyond 5.
    private static int RunPenalty(bool[] line)
    {
        int penalty = 0;
        foreach (var (_, length) in Runs(line))
            if (length >= 5) penalty += 3 + (length - 5);
        return penalty;
    }

    // dark:light:dark:light:dark = 1:1:3:1:1 (any unit), with a light area of 4 units on one side
    // and at least 1 unit on the other; each qualifying side counts. Outside the symbol is light.
    private static int FinderLikePatterns(bool[] line)
    {
        var runs = new List<(bool Dark, int Length)> { (false, line.Length) }; // quiet zone, effectively unlimited
        foreach (var run in Runs(line))
        {
            if (!run.Dark && !runs[^1].Dark) runs[^1] = (false, runs[^1].Length + run.Length);
            else runs.Add(run);
        }
        if (runs[^1].Dark) runs.Add((false, line.Length));
        else runs[^1] = (false, runs[^1].Length + line.Length);

        int count = 0;
        for (int i = 1; i + 5 < runs.Count; i++)
        {
            if (!runs[i].Dark) continue;
            int u = runs[i].Length;
            int[] expected = { u, u, 3 * u, u, u };
            if (!expected.Select((len, k) => runs[i + k].Length == len).All(ok => ok)) continue;
            int before = runs[i - 1].Length, after = runs[i + 5].Length;
            if (before >= 4 * u && after >= u) count++;
            if (after >= 4 * u && before >= u) count++;
        }
        return count;
    }

    private static IEnumerable<(bool Dark, int Length)> Runs(bool[] line)
    {
        int start = 0;
        for (int i = 1; i <= line.Length; i++)
        {
            if (i == line.Length || line[i] != line[start])
            {
                yield return (line[start], i - start);
                start = i;
            }
        }
    }
}
