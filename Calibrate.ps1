param(
    [Parameter(Mandatory = $true)][string]$Reference,
    [Parameter(Mandatory = $true)][string]$Amd,
    [string]$Output = (Join-Path (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AmdHdrScreenshotFixer') 'calibration.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class LutCalibrationResult
{
    public int[] Red { get; set; }
    public int[] Green { get; set; }
    public int[] Blue { get; set; }
    public long TotalPixels { get; set; }
    public long StablePixels { get; set; }
    public double OriginalMae { get; set; }
    public double CalibratedMae { get; set; }
}

public static class ScreenshotLutCalibrator
{
    private static Bitmap ToArgb(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(source, 0, 0);
        return result;
    }

    private static byte[] ReadPixels(Bitmap bitmap, out BitmapData bits)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        bits = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[Math.Abs(bits.Stride) * bitmap.Height];
        Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
        return pixels;
    }

    private static int[] BuildLut(long[,] histogram)
    {
        var raw = new double[256];
        var weights = new double[256];
        for (int input = 0; input < 256; input++)
        {
            long total = 0;
            for (int output = 0; output < 256; output++) total += histogram[input, output];
            weights[input] = Math.Max(1, total);
            if (total == 0) { raw[input] = input; continue; }

            long middle = (total + 1) / 2;
            long cumulative = 0;
            for (int output = 0; output < 256; output++)
            {
                cumulative += histogram[input, output];
                if (cumulative >= middle) { raw[input] = output; break; }
            }
        }

        // Weighted isotonic regression keeps the learned transfer curve monotonic.
        var level = new double[256];
        var weight = new double[256];
        var start = new int[256];
        var end = new int[256];
        int blocks = 0;
        for (int i = 0; i < 256; i++)
        {
            level[blocks] = raw[i]; weight[blocks] = weights[i]; start[blocks] = i; end[blocks] = i;
            blocks++;
            while (blocks > 1 && level[blocks - 2] > level[blocks - 1])
            {
                double combined = weight[blocks - 2] + weight[blocks - 1];
                level[blocks - 2] = (level[blocks - 2] * weight[blocks - 2] +
                    level[blocks - 1] * weight[blocks - 1]) / combined;
                weight[blocks - 2] = combined;
                end[blocks - 2] = end[blocks - 1];
                blocks--;
            }
        }

        var lut = new int[256];
        for (int block = 0; block < blocks; block++)
            for (int i = start[block]; i <= end[block]; i++)
                lut[i] = Math.Max(0, Math.Min(255, (int)Math.Round(level[block])));
        return lut;
    }

    private static void Add(long[,] histogram, int input, int output)
    {
        histogram[input, output]++;
    }

    public static LutCalibrationResult Fit(string amdPath, string referencePath)
    {
        using (var amdSource = new Bitmap(amdPath))
        using (var referenceSource = new Bitmap(referencePath))
        {
            if (amdSource.Width != referenceSource.Width || amdSource.Height != referenceSource.Height)
                throw new ArgumentException("AMD and reference images must have identical dimensions");

            using (var amd = ToArgb(amdSource))
            using (var reference = ToArgb(referenceSource))
            {
                BitmapData amdBits, referenceBits;
                byte[] a = ReadPixels(amd, out amdBits);
                byte[] r = ReadPixels(reference, out referenceBits);
                try
                {
                    long[,] hr = new long[256, 256], hg = new long[256, 256], hb = new long[256, 256];
                    long originalError = 0;
                    long totalPixels = (long)amd.Width * amd.Height;

                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(amdBits.Stride);
                        int ri = y * Math.Abs(referenceBits.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                        {
                            Add(hb, a[ai], r[ri]); Add(hg, a[ai + 1], r[ri + 1]); Add(hr, a[ai + 2], r[ri + 2]);
                            originalError += Math.Abs(a[ai] - r[ri]) + Math.Abs(a[ai + 1] - r[ri + 1]) + Math.Abs(a[ai + 2] - r[ri + 2]);
                        }
                    }

                    int[] lr = BuildLut(hr), lg = BuildLut(hg), lb = BuildLut(hb);
                    hr = new long[256, 256]; hg = new long[256, 256]; hb = new long[256, 256];
                    long stablePixels = 0;

                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(amdBits.Stride);
                        int ri = y * Math.Abs(referenceBits.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                        {
                            int residual = Math.Abs(lb[a[ai]] - r[ri]) + Math.Abs(lg[a[ai + 1]] - r[ri + 1]) + Math.Abs(lr[a[ai + 2]] - r[ri + 2]);
                            if (residual <= 24)
                            {
                                Add(hb, a[ai], r[ri]); Add(hg, a[ai + 1], r[ri + 1]); Add(hr, a[ai + 2], r[ri + 2]);
                                stablePixels++;
                            }
                        }
                    }

                    lr = BuildLut(hr); lg = BuildLut(hg); lb = BuildLut(hb);
                    long calibratedError = 0;
                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(amdBits.Stride);
                        int ri = y * Math.Abs(referenceBits.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                            calibratedError += Math.Abs(lb[a[ai]] - r[ri]) + Math.Abs(lg[a[ai + 1]] - r[ri + 1]) + Math.Abs(lr[a[ai + 2]] - r[ri + 2]);
                    }

                    return new LutCalibrationResult {
                        Red = lr, Green = lg, Blue = lb, TotalPixels = totalPixels,
                        StablePixels = stablePixels,
                        OriginalMae = originalError / (totalPixels * 3.0),
                        CalibratedMae = calibratedError / (totalPixels * 3.0)
                    };
                }
                finally
                {
                    amd.UnlockBits(amdBits);
                    reference.UnlockBits(referenceBits);
                }
            }
        }
    }
}

public sealed class LuminanceCalibrationResult
{
    public int[] Luma { get; set; }
    public double Saturation { get; set; }
    public long TotalPixels { get; set; }
    public long StablePixels { get; set; }
    public double OriginalMae { get; set; }
    public double CalibratedMae { get; set; }
}

public static class LuminanceLutCalibrator
{
    private static int Luma(byte b, byte g, byte r)
    {
        return Math.Max(0, Math.Min(255, (int)Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b)));
    }

    private static byte Clamp(double value)
    {
        return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value)));
    }

    private static int[] BuildLut(long[,] histogram)
    {
        var raw = new double[256];
        var weights = new double[256];
        for (int input = 0; input < 256; input++)
        {
            long total = 0;
            for (int output = 0; output < 256; output++) total += histogram[input, output];
            weights[input] = Math.Max(1, total);
            if (total == 0) { raw[input] = input; continue; }
            long middle = (total + 1) / 2, cumulative = 0;
            for (int output = 0; output < 256; output++)
            {
                cumulative += histogram[input, output];
                if (cumulative >= middle) { raw[input] = output; break; }
            }
        }

        var level = new double[256]; var weight = new double[256];
        var start = new int[256]; var end = new int[256]; int blocks = 0;
        for (int i = 0; i < 256; i++)
        {
            level[blocks] = raw[i]; weight[blocks] = weights[i]; start[blocks] = i; end[blocks] = i; blocks++;
            while (blocks > 1 && level[blocks - 2] > level[blocks - 1])
            {
                double combined = weight[blocks - 2] + weight[blocks - 1];
                level[blocks - 2] = (level[blocks - 2] * weight[blocks - 2] + level[blocks - 1] * weight[blocks - 1]) / combined;
                weight[blocks - 2] = combined; end[blocks - 2] = end[blocks - 1]; blocks--;
            }
        }
        var lut = new int[256];
        for (int block = 0; block < blocks; block++)
            for (int i = start[block]; i <= end[block]; i++)
                lut[i] = Math.Max(0, Math.Min(255, (int)Math.Round(level[block])));
        return lut;
    }

    private static void Transform(byte b, byte g, byte r, int[] lut, double saturation,
        out byte ob, out byte og, out byte orr)
    {
        int y = Luma(b, g, r);
        double targetY = lut[y];
        double ratio = y > 0 ? targetY / y : 0.0;
        double baseB = b * ratio, baseG = g * ratio, baseR = r * ratio;
        ob = Clamp(targetY + (baseB - targetY) * saturation);
        og = Clamp(targetY + (baseG - targetY) * saturation);
        orr = Clamp(targetY + (baseR - targetY) * saturation);
    }

    public static LuminanceCalibrationResult Fit(string amdPath, string referencePath)
    {
        using (var amdSource = new Bitmap(amdPath))
        using (var refSource = new Bitmap(referencePath))
        {
            if (amdSource.Width != refSource.Width || amdSource.Height != refSource.Height)
                throw new ArgumentException("AMD and reference images must have identical dimensions");
            using (var amd = new Bitmap(amdSource.Width, amdSource.Height, PixelFormat.Format32bppArgb))
            using (var reference = new Bitmap(refSource.Width, refSource.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(amd)) g.DrawImageUnscaled(amdSource, 0, 0);
                using (var g = Graphics.FromImage(reference)) g.DrawImageUnscaled(refSource, 0, 0);
                var rect = new Rectangle(0, 0, amd.Width, amd.Height);
                var ab = amd.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var rb = reference.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var ap = new byte[Math.Abs(ab.Stride) * amd.Height];
                    var rp = new byte[Math.Abs(rb.Stride) * reference.Height];
                    Marshal.Copy(ab.Scan0, ap, 0, ap.Length); Marshal.Copy(rb.Scan0, rp, 0, rp.Length);
                    var histogram = new long[256, 256]; long originalError = 0;
                    long total = (long)amd.Width * amd.Height;
                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(ab.Stride), ri = y * Math.Abs(rb.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                        {
                            histogram[Luma(ap[ai], ap[ai + 1], ap[ai + 2]), Luma(rp[ri], rp[ri + 1], rp[ri + 2])]++;
                            originalError += Math.Abs(ap[ai] - rp[ri]) + Math.Abs(ap[ai + 1] - rp[ri + 1]) + Math.Abs(ap[ai + 2] - rp[ri + 2]);
                        }
                    }
                    int[] lut = BuildLut(histogram);

                    double bestSaturation = 1.0, bestError = double.MaxValue;
                    for (int si = 70; si <= 180; si++)
                    {
                        double saturation = si / 100.0, error = 0; long samples = 0;
                        for (int y = 0; y < amd.Height; y += 4)
                        {
                            int ai = y * Math.Abs(ab.Stride), ri = y * Math.Abs(rb.Stride);
                            for (int x = 0; x < amd.Width; x += 4)
                            {
                                int ao = ai + x * 4, ro = ri + x * 4; byte b, g, r;
                                Transform(ap[ao], ap[ao + 1], ap[ao + 2], lut, saturation, out b, out g, out r);
                                error += Math.Abs(b - rp[ro]) + Math.Abs(g - rp[ro + 1]) + Math.Abs(r - rp[ro + 2]); samples++;
                            }
                        }
                        error /= samples * 3.0;
                        if (error < bestError) { bestError = error; bestSaturation = saturation; }
                    }

                    long calibratedError = 0, stable = 0;
                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(ab.Stride), ri = y * Math.Abs(rb.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                        {
                            byte b, g, r; Transform(ap[ai], ap[ai + 1], ap[ai + 2], lut, bestSaturation, out b, out g, out r);
                            int error = Math.Abs(b - rp[ri]) + Math.Abs(g - rp[ri + 1]) + Math.Abs(r - rp[ri + 2]);
                            calibratedError += error; if (error <= 24) stable++;
                        }
                    }
                    return new LuminanceCalibrationResult {
                        Luma = lut, Saturation = bestSaturation, TotalPixels = total, StablePixels = stable,
                        OriginalMae = originalError / (total * 3.0), CalibratedMae = calibratedError / (total * 3.0)
                    };
                }
                finally { amd.UnlockBits(ab); reference.UnlockBits(rb); }
            }
        }
    }
}

public sealed class PolynomialCalibrationResult
{
    public double[] Red { get; set; }
    public double[] Green { get; set; }
    public double[] Blue { get; set; }
    public long TotalPixels { get; set; }
    public long StablePixels { get; set; }
    public double OriginalMae { get; set; }
    public double CalibratedMae { get; set; }
}

public static class PolynomialColorCalibrator
{
    private static double[] Features(byte b, byte g, byte r)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        return new[] { 1.0, rn, gn, bn, rn * rn, gn * gn, bn * bn, rn * gn, rn * bn, gn * bn };
    }

    private static double[] Solve(double[,] matrix, double[] vector)
    {
        int n = vector.Length;
        var augmented = new double[n, n + 1];
        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++) augmented[row, col] = matrix[row, col];
            augmented[row, n] = vector[row];
        }
        for (int pivot = 0; pivot < n; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < n; row++)
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (Math.Abs(augmented[best, pivot]) < 1e-12) throw new InvalidOperationException("Calibration matrix is singular");
            if (best != pivot)
                for (int col = pivot; col <= n; col++) { double t = augmented[pivot, col]; augmented[pivot, col] = augmented[best, col]; augmented[best, col] = t; }
            double scale = augmented[pivot, pivot];
            for (int col = pivot; col <= n; col++) augmented[pivot, col] /= scale;
            for (int row = 0; row < n; row++)
            {
                if (row == pivot) continue;
                double factor = augmented[row, pivot];
                for (int col = pivot; col <= n; col++) augmented[row, col] -= factor * augmented[pivot, col];
            }
        }
        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = augmented[i, n];
        return result;
    }

    private static byte Evaluate(double[] coefficients, double[] features)
    {
        double value = 0;
        for (int i = 0; i < coefficients.Length; i++) value += coefficients[i] * features[i];
        return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * 255.0)));
    }

    public static PolynomialCalibrationResult Fit(string amdPath, string referencePath)
    {
        using (var amdSource = new Bitmap(amdPath))
        using (var refSource = new Bitmap(referencePath))
        {
            if (amdSource.Width != refSource.Width || amdSource.Height != refSource.Height)
                throw new ArgumentException("AMD and reference images must have identical dimensions");
            using (var amd = new Bitmap(amdSource.Width, amdSource.Height, PixelFormat.Format32bppArgb))
            using (var reference = new Bitmap(refSource.Width, refSource.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(amd)) g.DrawImageUnscaled(amdSource, 0, 0);
                using (var g = Graphics.FromImage(reference)) g.DrawImageUnscaled(refSource, 0, 0);
                var rect = new Rectangle(0, 0, amd.Width, amd.Height);
                var ab = amd.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var rb = reference.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var ap = new byte[Math.Abs(ab.Stride) * amd.Height];
                    var rp = new byte[Math.Abs(rb.Stride) * reference.Height];
                    Marshal.Copy(ab.Scan0, ap, 0, ap.Length); Marshal.Copy(rb.Scan0, rp, 0, rp.Length);
                    const int featureCount = 10;
                    var xtx = new double[featureCount, featureCount];
                    var xtRed = new double[featureCount]; var xtGreen = new double[featureCount]; var xtBlue = new double[featureCount];
                    for (int y = 0; y < amd.Height; y += 2)
                    {
                        int ai = y * Math.Abs(ab.Stride), ri = y * Math.Abs(rb.Stride);
                        for (int x = 0; x < amd.Width; x += 2)
                        {
                            int ao = ai + x * 4, ro = ri + x * 4;
                            double[] f = Features(ap[ao], ap[ao + 1], ap[ao + 2]);
                            for (int i = 0; i < featureCount; i++)
                            {
                                xtRed[i] += f[i] * rp[ro + 2] / 255.0;
                                xtGreen[i] += f[i] * rp[ro + 1] / 255.0;
                                xtBlue[i] += f[i] * rp[ro] / 255.0;
                                for (int j = 0; j < featureCount; j++) xtx[i, j] += f[i] * f[j];
                            }
                        }
                    }
                    double[] cr = Solve(xtx, xtRed), cg = Solve(xtx, xtGreen), cb = Solve(xtx, xtBlue);
                    long originalError = 0, calibratedError = 0, stable = 0;
                    long total = (long)amd.Width * amd.Height;
                    for (int y = 0; y < amd.Height; y++)
                    {
                        int ai = y * Math.Abs(ab.Stride), ri = y * Math.Abs(rb.Stride);
                        for (int x = 0; x < amd.Width; x++, ai += 4, ri += 4)
                        {
                            double[] f = Features(ap[ai], ap[ai + 1], ap[ai + 2]);
                            byte r = Evaluate(cr, f), g = Evaluate(cg, f), b = Evaluate(cb, f);
                            int error = Math.Abs(b - rp[ri]) + Math.Abs(g - rp[ri + 1]) + Math.Abs(r - rp[ri + 2]);
                            calibratedError += error; if (error <= 24) stable++;
                            originalError += Math.Abs(ap[ai] - rp[ri]) + Math.Abs(ap[ai + 1] - rp[ri + 1]) + Math.Abs(ap[ai + 2] - rp[ri + 2]);
                        }
                    }
                    return new PolynomialCalibrationResult {
                        Red = cr, Green = cg, Blue = cb, TotalPixels = total, StablePixels = stable,
                        OriginalMae = originalError / (total * 3.0), CalibratedMae = calibratedError / (total * 3.0)
                    };
                }
                finally { amd.UnlockBits(ab); reference.UnlockBits(rb); }
            }
        }
    }
}

public static class HistogramColorCalibrator
{
    private static int[] Match(long[] source, long[] target)
    {
        long sourceTotal = 0, targetTotal = 0;
        for (int i = 0; i < 256; i++) { sourceTotal += source[i]; targetTotal += target[i]; }
        var targetCdf = new double[256]; long cumulative = 0;
        for (int i = 0; i < 256; i++) { cumulative += target[i]; targetCdf[i] = cumulative / (double)targetTotal; }
        var lut = new int[256]; long before = 0; int targetValue = 0;
        for (int sourceValue = 0; sourceValue < 256; sourceValue++)
        {
            double quantile = (before + source[sourceValue] * 0.5) / sourceTotal;
            while (targetValue < 255 && targetCdf[targetValue] < quantile) targetValue++;
            lut[sourceValue] = targetValue;
            before += source[sourceValue];
        }
        return lut;
    }

    public static LutCalibrationResult Fit(string amdPath, string referencePath)
    {
        using (var amd = new Bitmap(amdPath))
        using (var reference = new Bitmap(referencePath))
        {
            if (amd.Width != reference.Width || amd.Height != reference.Height)
                throw new ArgumentException("AMD and reference images must have identical dimensions");
            var ah = new[] { new long[256], new long[256], new long[256] };
            var rh = new[] { new long[256], new long[256], new long[256] };
            for (int y = 0; y < amd.Height; y++)
                for (int x = 0; x < amd.Width; x++)
                {
                    Color a = amd.GetPixel(x, y), r = reference.GetPixel(x, y);
                    ah[0][a.R]++; ah[1][a.G]++; ah[2][a.B]++;
                    rh[0][r.R]++; rh[1][r.G]++; rh[2][r.B]++;
                }
            int[] lr = Match(ah[0], rh[0]), lg = Match(ah[1], rh[1]), lb = Match(ah[2], rh[2]);
            long originalError = 0, calibratedError = 0, stable = 0;
            long total = (long)amd.Width * amd.Height;
            for (int y = 0; y < amd.Height; y++)
                for (int x = 0; x < amd.Width; x++)
                {
                    Color a = amd.GetPixel(x, y), r = reference.GetPixel(x, y);
                    int error = Math.Abs(lr[a.R] - r.R) + Math.Abs(lg[a.G] - r.G) + Math.Abs(lb[a.B] - r.B);
                    calibratedError += error; if (error <= 24) stable++;
                    originalError += Math.Abs(a.R - r.R) + Math.Abs(a.G - r.G) + Math.Abs(a.B - r.B);
                }
            return new LutCalibrationResult {
                Red = lr, Green = lg, Blue = lb, TotalPixels = total, StablePixels = stable,
                OriginalMae = originalError / (total * 3.0), CalibratedMae = calibratedError / (total * 3.0)
            };
        }
    }
}
'@

if (-not ('ScreenshotLutCalibrator' -as [type])) {
    Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
}

$result = [HistogramColorCalibrator]::Fit(
    [IO.Path]::GetFullPath($Amd),
    [IO.Path]::GetFullPath($Reference)
)

$calibration = [ordered]@{
    mode = 'histogram'
    source = 'per-channel histogram matching LUT'
    reference = [IO.Path]::GetFileName($Reference)
    amd = [IO.Path]::GetFileName($Amd)
    totalPixels = $result.TotalPixels
    stablePixels = $result.StablePixels
    originalMae = [Math]::Round($result.OriginalMae, 4)
    calibratedMae = [Math]::Round($result.CalibratedMae, 4)
    red = $result.Red
    green = $result.Green
    blue = $result.Blue
}

$outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}
$calibration | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath $Output
([pscustomobject]$calibration) | Select-Object source, totalPixels, stablePixels, originalMae, calibratedMae | Format-List
Write-Host "Saved: $Output"
