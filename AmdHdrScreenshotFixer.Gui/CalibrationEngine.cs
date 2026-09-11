using System.IO;

namespace AmdHdrScreenshotFixer;

public static class CalibrationEngine
{
    private const int SamplesPerPair = 180_000;
    private static readonly (int GridSize, int SmoothPasses)[] Candidates = [(9, 2), (17, 2), (17, 1)];

    public static CalibrationResult Fit(IReadOnlyList<CalibrationPair> pairs, CalibrationData baseline,
        IProgress<CalibrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (pairs.Count < 3) throw new ArgumentException("至少需要三组对比图。", nameof(pairs));
        if (baseline.Red?.Length != 256 || baseline.Green?.Length != 256 || baseline.Blue?.Length != 256)
            throw new InvalidDataException("默认模型不是可组合的 RGB 标定表。");

        var samples = new List<List<ColorSample>>(pairs.Count);
        for (var index = 0; index < pairs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CalibrationProgress(index * 20 / pairs.Count,
                $"读取第 {index + 1}/{pairs.Count} 组图片"));
            samples.Add(LoadSamples(pairs[index], cancellationToken));
        }

        CandidateScore? best = null;
        for (var candidateIndex = 0; candidateIndex < Candidates.Length; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Candidates[candidateIndex];
            var foldScores = new List<ScoreResult>();
            for (var validationIndex = 0; validationIndex < samples.Count; validationIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var training = samples.Where((_, index) => index != validationIndex).SelectMany(item => item);
                var lut = FitLut(training, baseline, candidate.GridSize, candidate.SmoothPasses, cancellationToken);
                foldScores.Add(Score(samples[validationIndex], candidate.GridSize, lut, cancellationToken));
            }

            var score = Average(foldScores);
            var current = new CandidateScore(candidate.GridSize, candidate.SmoothPasses, score);
            if (best is null || current.Score.MeanDeltaE < best.Score.MeanDeltaE) best = current;
            progress?.Report(new CalibrationProgress(20 + (candidateIndex + 1) * 55 / Candidates.Length,
                $"{candidate.GridSize}³ LUT：平均 ΔE {score.MeanDeltaE:F2}，P95 {score.P95DeltaE:F2}"));
            if (MeetsTarget(score)) break;
        }

        var selected = best ?? throw new InvalidOperationException("没有生成可用的校准候选。");
        progress?.Report(new CalibrationProgress(82, "使用全部样本生成最终模型"));
        var finalLut = FitLut(samples.SelectMany(item => item), baseline, selected.GridSize, selected.SmoothPasses,
            cancellationToken);
        var quality = new CalibrationQuality
        {
            MeanDeltaE = Math.Round(selected.Score.MeanDeltaE, 3),
            P95DeltaE = Math.Round(selected.Score.P95DeltaE, 3),
            Score = Math.Round(Math.Clamp(100 - selected.Score.MeanDeltaE * 7 - selected.Score.P95DeltaE * 1.5,
                0, 100), 1),
            MeetsTarget = MeetsTarget(selected.Score),
            PairCount = pairs.Count
        };
        var calibration = new CalibrationData
        {
            Mode = "lut3d",
            GridSize = selected.GridSize,
            Lut3D = finalLut,
            Quality = quality
        };
        progress?.Report(new CalibrationProgress(100, quality.MeetsTarget ? "校准达到目标" : "已生成当前最佳结果"));
        return new CalibrationResult(calibration, quality, selected.GridSize);
    }

    private static List<ColorSample> LoadSamples(CalibrationPair pair, CancellationToken cancellationToken)
    {
        if (!File.Exists(pair.AmdPath) || !File.Exists(pair.ReferencePath))
            throw new FileNotFoundException("对比图文件不存在。");
        var amd = ImageProcessor.LoadBase(pair.AmdPath, new FixerConfig(), null);
        var reference = ImageProcessor.LoadBase(pair.ReferencePath, new FixerConfig(), null);
        if (amd.Width != reference.Width || amd.Height != reference.Height)
            throw new InvalidDataException($"对比图尺寸不同：{Path.GetFileName(pair.AmdPath)} 与 {Path.GetFileName(pair.ReferencePath)}");

        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((long)amd.Width * amd.Height / (double)SamplesPerPair)));
        var result = new List<ColorSample>(Math.Min(SamplesPerPair, amd.Width * amd.Height));
        for (var y = 0; y < amd.Height; y += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < amd.Width; x += step)
            {
                var offset = y * amd.Stride + x * 4;
                result.Add(new ColorSample(amd.Pixels[offset + 2], amd.Pixels[offset + 1], amd.Pixels[offset],
                    reference.Pixels[offset + 2], reference.Pixels[offset + 1], reference.Pixels[offset]));
            }
        }
        return result;
    }

    private static double[] FitLut(IEnumerable<ColorSample> samples, CalibrationData baseline,
        int gridSize, int smoothPasses,
        CancellationToken cancellationToken)
    {
        var nodeCount = gridSize * gridSize * gridSize;
        var weights = new double[nodeCount];
        var sums = new double[nodeCount * 3];
        var processed = 0;
        foreach (var sample in samples)
        {
            if ((processed++ & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            Accumulate(sample, gridSize, weights, sums);
        }

        var lut = new double[nodeCount * 3];
        for (var r = 0; r < gridSize; r++)
        for (var g = 0; g < gridSize; g++)
        for (var b = 0; b < gridSize; b++)
        {
            var node = (r * gridSize + g) * gridSize + b;
            var index = node * 3;
            if (weights[node] > 0.001)
            {
                lut[index] = sums[index] / weights[node];
                lut[index + 1] = sums[index + 1] / weights[node];
                lut[index + 2] = sums[index + 2] / weights[node];
            }
            else
            {
                lut[index] = EvaluateBaseline(r / (double)(gridSize - 1), baseline.Red!);
                lut[index + 1] = EvaluateBaseline(g / (double)(gridSize - 1), baseline.Green!);
                lut[index + 2] = EvaluateBaseline(b / (double)(gridSize - 1), baseline.Blue!);
            }
        }

        for (var pass = 0; pass < smoothPasses; pass++) lut = Smooth(lut, weights, gridSize);
        for (var index = 0; index < lut.Length; index++) lut[index] = Math.Clamp(lut[index], 0, 1);
        return lut;
    }

    private static double EvaluateBaseline(double value, double[] table)
    {
        var position = Math.Clamp(value, 0, 1) * 255;
        var lower = Math.Min((int)position, 254);
        var fraction = position - lower;
        return (table[lower] * (1 - fraction) + table[lower + 1] * fraction) / 255.0;
    }

    private static void Accumulate(ColorSample sample, int gridSize, double[] weights, double[] sums)
    {
        var rf = sample.R / 255.0 * (gridSize - 1); var r0 = Math.Min((int)rf, gridSize - 2); var rt = rf - r0;
        var gf = sample.G / 255.0 * (gridSize - 1); var g0 = Math.Min((int)gf, gridSize - 2); var gt = gf - g0;
        var bf = sample.B / 255.0 * (gridSize - 1); var b0 = Math.Min((int)bf, gridSize - 2); var bt = bf - b0;
        for (var dr = 0; dr <= 1; dr++)
        for (var dg = 0; dg <= 1; dg++)
        for (var db = 0; db <= 1; db++)
        {
            var weight = (dr == 0 ? 1 - rt : rt) * (dg == 0 ? 1 - gt : gt) * (db == 0 ? 1 - bt : bt);
            var node = ((r0 + dr) * gridSize + g0 + dg) * gridSize + b0 + db;
            var index = node * 3;
            weights[node] += weight;
            sums[index] += sample.TargetR / 255.0 * weight;
            sums[index + 1] += sample.TargetG / 255.0 * weight;
            sums[index + 2] += sample.TargetB / 255.0 * weight;
        }
    }

    private static double[] Smooth(double[] source, double[] weights, int gridSize)
    {
        var output = (double[])source.Clone();
        int[][] directions = [[-1, 0, 0], [1, 0, 0], [0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1]];
        for (var r = 0; r < gridSize; r++)
        for (var g = 0; g < gridSize; g++)
        for (var b = 0; b < gridSize; b++)
        {
            var node = (r * gridSize + g) * gridSize + b;
            double nr = 0, ng = 0, nb = 0; var count = 0;
            foreach (var direction in directions)
            {
                var rr = r + direction[0]; var gg = g + direction[1]; var bb = b + direction[2];
                if (rr < 0 || rr >= gridSize || gg < 0 || gg >= gridSize || bb < 0 || bb >= gridSize) continue;
                var index = ((rr * gridSize + gg) * gridSize + bb) * 3;
                nr += source[index]; ng += source[index + 1]; nb += source[index + 2]; count++;
            }
            if (count == 0) continue;
            var own = node * 3;
            var blend = weights[node] > 1 ? 0.10 : 0.35;
            output[own] = source[own] * (1 - blend) + nr / count * blend;
            output[own + 1] = source[own + 1] * (1 - blend) + ng / count * blend;
            output[own + 2] = source[own + 2] * (1 - blend) + nb / count * blend;
        }
        return output;
    }

    private static ScoreResult Score(IReadOnlyList<ColorSample> samples, int gridSize, double[] lut,
        CancellationToken cancellationToken)
    {
        var errors = new double[samples.Count];
        double total = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            if ((index & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[index];
            var predicted = ImageProcessor.EvaluateLut3D(sample.R / 255.0, sample.G / 255.0,
                sample.B / 255.0, gridSize, lut);
            var error = DeltaE2000(ToLab(predicted.R, predicted.G, predicted.B),
                ToLab(sample.TargetR / 255.0, sample.TargetG / 255.0, sample.TargetB / 255.0));
            errors[index] = error;
            total += error;
        }
        Array.Sort(errors);
        return new ScoreResult(total / errors.Length, errors[Math.Min(errors.Length - 1, (int)(errors.Length * 0.95))]);
    }

    private static ScoreResult Average(IReadOnlyList<ScoreResult> scores) =>
        new(scores.Average(item => item.MeanDeltaE), scores.Average(item => item.P95DeltaE));

    private static bool MeetsTarget(ScoreResult score) => score.MeanDeltaE <= 3.0 && score.P95DeltaE <= 8.0;

    private static Lab ToLab(double r, double g, double b)
    {
        static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        r = Linear(Math.Clamp(r, 0, 1)); g = Linear(Math.Clamp(g, 0, 1)); b = Linear(Math.Clamp(b, 0, 1));
        var x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        var z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;
        static double F(double value) => value > 0.008856 ? Math.Cbrt(value) : 7.787 * value + 16.0 / 116.0;
        var fx = F(x); var fy = F(y); var fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double DeltaE2000(Lab first, Lab second)
    {
        var c1 = Math.Sqrt(first.A * first.A + first.B * first.B);
        var c2 = Math.Sqrt(second.A * second.A + second.B * second.B);
        var meanC = (c1 + c2) / 2;
        var g = 0.5 * (1 - Math.Sqrt(Math.Pow(meanC, 7) / (Math.Pow(meanC, 7) + Math.Pow(25.0, 7))));
        var a1 = (1 + g) * first.A; var a2 = (1 + g) * second.A;
        var cp1 = Math.Sqrt(a1 * a1 + first.B * first.B); var cp2 = Math.Sqrt(a2 * a2 + second.B * second.B);
        static double Hue(double a, double b) { var value = Math.Atan2(b, a) * 180 / Math.PI; return value < 0 ? value + 360 : value; }
        var h1 = Hue(a1, first.B); var h2 = Hue(a2, second.B);
        var dl = second.L - first.L; var dc = cp2 - cp1;
        var dh = cp1 * cp2 == 0 ? 0 : h2 - h1;
        if (dh > 180) dh -= 360; else if (dh < -180) dh += 360;
        var dBigH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(dh * Math.PI / 360);
        var meanL = (first.L + second.L) / 2; var meanCp = (cp1 + cp2) / 2;
        var meanH = cp1 * cp2 == 0 ? h1 + h2 : Math.Abs(h1 - h2) <= 180 ? (h1 + h2) / 2 :
            (h1 + h2 < 360 ? h1 + h2 + 360 : h1 + h2 - 360) / 2;
        var t = 1 - 0.17 * Math.Cos((meanH - 30) * Math.PI / 180) + 0.24 * Math.Cos(2 * meanH * Math.PI / 180)
            + 0.32 * Math.Cos((3 * meanH + 6) * Math.PI / 180) - 0.20 * Math.Cos((4 * meanH - 63) * Math.PI / 180);
        var sl = 1 + 0.015 * Math.Pow(meanL - 50, 2) / Math.Sqrt(20 + Math.Pow(meanL - 50, 2));
        var sc = 1 + 0.045 * meanCp; var sh = 1 + 0.015 * meanCp * t;
        var rt = -2 * Math.Sqrt(Math.Pow(meanCp, 7) / (Math.Pow(meanCp, 7) + Math.Pow(25.0, 7))) *
            Math.Sin(60 * Math.Exp(-Math.Pow((meanH - 275) / 25, 2)) * Math.PI / 180);
        var l = dl / sl; var c = dc / sc; var h = dBigH / sh;
        return Math.Sqrt(l * l + c * c + h * h + rt * c * h);
    }

    private readonly record struct ColorSample(byte R, byte G, byte B, byte TargetR, byte TargetG, byte TargetB);
    private readonly record struct Lab(double L, double A, double B);
    private readonly record struct ScoreResult(double MeanDeltaE, double P95DeltaE);
    private sealed record CandidateScore(int GridSize, int SmoothPasses, ScoreResult Score);
}
