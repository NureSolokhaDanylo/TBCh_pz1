using System.Diagnostics;
using System.Globalization;
using System.Text;
using Blockchain.Shared.Common;
using Blockchain.Shared.Models;

namespace Miner.Services;

public record DifficultyBenchmarkStats(
    int Difficulty,
    int RunsCount,
    double MeanTimeMs,
    double StdDevTimeMs,
    double MinTimeMs,
    double MaxTimeMs,
    double MeanIterations,
    IReadOnlyList<double> IndividualTimesMs
);

/// <summary>
/// Conducts Proof-of-Work mining performance experiments across multiple difficulty levels (e.g., D = 1, 2, 3, 4),
/// computes mean time and standard deviation over N runs per level, exports CSV data,
/// and generates a logarithmic-scale chart (SVG) illustrating the 16^D exponential scaling as a straight line.
/// </summary>
public class MiningBenchmarkService
{
    public IReadOnlyList<DifficultyBenchmarkStats> RunBenchmark(
        int[]? difficulties = null,
        int runsPerDifficulty = 10,
        Action<int, int, double, long>? onRunCompleted = null)
    {
        difficulties ??= [1, 2, 3, 4];
        var results = new List<DifficultyBenchmarkStats>(difficulties.Length);

        // Warmup JIT
        var warmupHeader = new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.EmptyRoot, 1);
        warmupHeader.Mine(1);

        foreach (var diff in difficulties)
        {
            var timesMs = new List<double>(runsPerDifficulty);
            var iterations = new List<long>(runsPerDifficulty);

            for (int run = 1; run <= runsPerDifficulty; run++)
            {
                // Use unique deterministic seed data per run so each run explores an independent hash space
                var dummyTx = new Transaction(
                    sender: AccountLedger.CoinbaseAddress,
                    recipient: $"0x{run:x8}{diff:x8}000000000000000000000000",
                    amount: 50m + run,
                    nonce: run);

                var merkleRoot = MerkleTree.ComputeRoot([dummyTx]);
                var header = new BlockHeader(
                    index: run,
                    previousHash: GenesisBlock.GenesisHash,
                    merkleRoot: merkleRoot,
                    timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(diff * 1000 + run),
                    nonce: 0,
                    difficulty: diff,
                    hash: string.Empty);

                var sw = Stopwatch.StartNew();
                header.Mine(diff);
                sw.Stop();

                var elapsedMs = Math.Max(0.001, sw.Elapsed.TotalMilliseconds);
                timesMs.Add(elapsedMs);
                iterations.Add(header.Nonce + 1);

                onRunCompleted?.Invoke(diff, run, elapsedMs, header.Nonce + 1);
            }

            var meanTime = timesMs.Average();
            var variance = timesMs.Sum(t => Math.Pow(t - meanTime, 2)) / timesMs.Count;
            var stdDevTime = Math.Sqrt(variance);
            var meanIters = iterations.Average();

            results.Add(new DifficultyBenchmarkStats(
                Difficulty: diff,
                RunsCount: runsPerDifficulty,
                MeanTimeMs: meanTime,
                StdDevTimeMs: stdDevTime,
                MinTimeMs: timesMs.Min(),
                MaxTimeMs: timesMs.Max(),
                MeanIterations: meanIters,
                IndividualTimesMs: timesMs
            ));
        }

        return results;
    }

    /// <summary>
    /// Exports benchmark statistics and individual run measurements to a CSV file.
    /// </summary>
    public static void SaveCsv(IReadOnlyList<DifficultyBenchmarkStats> stats, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Difficulty,RunsCount,MeanTimeMs,StdDevTimeMs,MinTimeMs,MaxTimeMs,MeanIterations,Log10MeanTimeMs");

        foreach (var s in stats)
        {
            var log10Mean = Math.Log10(Math.Max(0.001, s.MeanTimeMs));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{s.Difficulty},{s.RunsCount},{s.MeanTimeMs:F4},{s.StdDevTimeMs:F4},{s.MinTimeMs:F4},{s.MaxTimeMs:F4},{s.MeanIterations:F1},{log10Mean:F4}"));
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Generates a vector SVG chart with a logarithmic Y-axis (Time in ms), error bars (+/- 1 StdDev),
    /// and a theoretical 16^D reference line.
    /// </summary>
    public static void SaveLogScaleSvgChart(IReadOnlyList<DifficultyBenchmarkStats> stats, string svgFilePath)
    {
        const int width = 900;
        const int height = 540;
        const int padLeft = 140;
        const int padRight = 60;
        const int padTop = 75;
        const int padBottom = 80;

        int plotW = width - padLeft - padRight;
        int plotH = height - padTop - padBottom;

        // Logarithmic Y range
        double minLogY = -2.0;
        double maxLogY = 4.0;

        var minVal = stats.Min(s => Math.Max(0.001, s.MeanTimeMs - s.StdDevTimeMs * 0.5));
        var maxVal = stats.Max(s => Math.Max(0.01, s.MeanTimeMs + s.StdDevTimeMs));
        minLogY = Math.Floor(Math.Log10(minVal)) - 0.5;
        maxLogY = Math.Ceiling(Math.Log10(maxVal)) + 0.5;

        double MapX(int idx) => stats.Count <= 1
            ? padLeft + plotW / 2.0
            : padLeft + (double)idx / (stats.Count - 1) * plotW;

        double MapY(double timeMs)
        {
            var clamped = Math.Max(Math.Pow(10, minLogY), timeMs);
            var logVal = Math.Log10(clamped);
            var norm = (logVal - minLogY) / (maxLogY - minLogY);
            return padTop + plotH - norm * plotH;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" style="background:#ffffff;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif;">""");

        // Background card
        sb.AppendLine($"""  <rect width="{width}" height="{height}" fill="#ffffff" />""");

        // Title
        sb.AppendLine($"""  <text x="{width / 2}" y="35" fill="#0f172a" font-size="18" font-weight="bold" text-anchor="middle">Залежність часу майнінгу Proof-of-Work від складності (логарифмічна шкала)</text>""");
        sb.AppendLine($"""  <text x="{width / 2}" y="55" fill="#64748b" font-size="13" text-anchor="middle">Середній час (мс) ± 1 стандартне відхилення ({stats.FirstOrDefault()?.RunsCount ?? 10} запусків на рівень, експонента 16^D виглядає лінійною)</text>""");

        // Horizontal grid lines (powers of 10)
        for (int exp = (int)Math.Ceiling(minLogY); exp <= (int)Math.Floor(maxLogY); exp++)
        {
            double val = Math.Pow(10, exp);
            double y = MapY(val);
            string label = val >= 1 ? $"{val:F0} мс" : $"{val:G2} мс";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <line x1="{padLeft}" y1="{y:F1}" x2="{width - padRight}" y2="{y:F1}" stroke="#e2e8f0" stroke-dasharray="4,4" stroke-width="1" />"""));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <text x="{padLeft - 14}" y="{y + 4:F1}" fill="#475569" font-size="12" font-weight="500" text-anchor="end">10^{exp} ({label})</text>"""));
        }

        // Axes
        sb.AppendLine($"""  <line x1="{padLeft}" y1="{padTop}" x2="{padLeft}" y2="{padTop + plotH}" stroke="#94a3b8" stroke-width="2" />""");
        sb.AppendLine($"""  <line x1="{padLeft}" y1="{padTop + plotH}" x2="{width - padRight}" y2="{padTop + plotH}" stroke="#94a3b8" stroke-width="2" />""");

        // Axis titles
        sb.AppendLine($"""  <text x="{padLeft + plotW / 2}" y="{height - 22}" fill="#0f172a" font-size="14" font-weight="bold" text-anchor="middle">Складність D (кількість провідних шістнадцяткових нулів)</text>""");
        sb.AppendLine($"""  <text x="35" y="{padTop + plotH / 2}" fill="#0f172a" font-size="14" font-weight="bold" text-anchor="middle" transform="rotate(-90 35,{padTop + plotH / 2})">Час майнінгу, мс (шкала Log10)</text>""");

        // Theoretical 16^D line anchored at the first point
        if (stats.Count > 1)
        {
            var pts = new List<string>();
            double baseTime = stats[0].MeanTimeMs;
            int baseDiff = stats[0].Difficulty;
            for (int i = 0; i < stats.Count; i++)
            {
                double theoTime = baseTime * Math.Pow(16, stats[i].Difficulty - baseDiff);
                pts.Add(string.Create(CultureInfo.InvariantCulture, $"{MapX(i):F1},{MapY(theoTime):F1}"));
            }
            sb.AppendLine($"""  <polyline fill="none" stroke="#d97706" stroke-width="2.5" stroke-dasharray="6,4" points="{string.Join(" ", pts)}" />""");
        }

        // Measured mean line
        if (stats.Count > 1)
        {
            var pts = stats.Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"{MapX(i):F1},{MapY(s.MeanTimeMs):F1}"));
            sb.AppendLine($"""  <polyline fill="none" stroke="#0284c7" stroke-width="3" points="{string.Join(" ", pts)}" />""");
        }

        // Error bars and points
        for (int i = 0; i < stats.Count; i++)
        {
            var s = stats[i];
            double x = MapX(i);
            double yMean = MapY(s.MeanTimeMs);
            double yHigh = MapY(s.MeanTimeMs + s.StdDevTimeMs);
            double yLow = MapY(Math.Max(s.MinTimeMs, s.MeanTimeMs - s.StdDevTimeMs * 0.8));

            // X-axis tick & label
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <line x1="{x:F1}" y1="{padTop + plotH}" x2="{x:F1}" y2="{padTop + plotH + 8}" stroke="#64748b" stroke-width="2" />"""));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <text x="{x:F1}" y="{padTop + plotH + 28}" fill="#0f172a" font-size="13" font-weight="bold" text-anchor="middle">D = {s.Difficulty} ({new string('0', s.Difficulty)}...)</text>"""));

            // Error bar (+/- StdDev)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <line x1="{x:F1}" y1="{yHigh:F1}" x2="{x:F1}" y2="{yLow:F1}" stroke="#e11d48" stroke-width="2" />"""));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <line x1="{x - 8:F1}" y1="{yHigh:F1}" x2="{x + 8:F1}" y2="{yHigh:F1}" stroke="#e11d48" stroke-width="2" />"""));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <line x1="{x - 8:F1}" y1="{yLow:F1}" x2="{x + 8:F1}" y2="{yLow:F1}" stroke="#e11d48" stroke-width="2" />"""));

            // Data point
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <circle cx="{x:F1}" cy="{yMean:F1}" r="6" fill="#0284c7" stroke="#ffffff" stroke-width="2" />"""));

            // Value label
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"""  <text x="{x:F1}" y="{yHigh - 10:F1}" fill="#0369a1" font-size="12" font-weight="bold" text-anchor="middle">{s.MeanTimeMs:F2} ± {s.StdDevTimeMs:F2} мс</text>"""));
        }

        // Legend
        sb.AppendLine($"""  <rect x="{padLeft + 15}" y="{padTop + 10}" width="280" height="70" rx="6" fill="#f8fafc" stroke="#cbd5e1" stroke-width="1.5" />""");
        sb.AppendLine($"""  <line x1="{padLeft + 25}" y1="{padTop + 32}" x2="{padLeft + 55}" y2="{padTop + 32}" stroke="#0284c7" stroke-width="3" />""");
        sb.AppendLine($"""  <text x="{padLeft + 65}" y="{padTop + 36}" fill="#0f172a" font-size="12" font-weight="500">Виміряний середній час (мс)</text>""");
        sb.AppendLine($"""  <line x1="{padLeft + 25}" y1="{padTop + 54}" x2="{padLeft + 55}" y2="{padTop + 54}" stroke="#d97706" stroke-width="2.5" stroke-dasharray="6,4" />""");
        sb.AppendLine($"""  <text x="{padLeft + 65}" y="{padTop + 58}" fill="#0f172a" font-size="12" font-weight="500">Теоретичне масштабування O(16^D)</text>""");

        sb.AppendLine("</svg>");

        var dir = Path.GetDirectoryName(svgFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(svgFilePath, sb.ToString(), Encoding.UTF8);
    }
}
