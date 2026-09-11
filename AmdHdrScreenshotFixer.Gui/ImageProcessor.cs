using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace AmdHdrScreenshotFixer;

public static class ImageProcessor
{
    public static PixelFrame LoadBase(string path, FixerConfig config, CalibrationData? calibration, int maxPreviewEdge = 0)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        if (maxPreviewEdge > 0 && Math.Max(source.PixelWidth, source.PixelHeight) > maxPreviewEdge)
        {
            var scale = maxPreviewEdge / (double)Math.Max(source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);
        ApplyBaseCorrection(pixels, config, calibration);
        return new PixelFrame(formatted.PixelWidth, formatted.PixelHeight, formatted.DpiX, formatted.DpiY, pixels, stride);
    }

    public static BitmapSource Render(PixelFrame frame, double redGain, double greenGain, double blueGain,
        double exposure = 0.0, double postContrast = 1.0, double postSaturation = 1.0,
        double postBlackPoint = 0.0, double postWhitePoint = 1.0)
    {
        var output = ApplyAdjustments(frame.Pixels, redGain, greenGain, blueGain, exposure, postContrast,
            postSaturation, postBlackPoint, postWhitePoint);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, frame.DpiX, frame.DpiY,
            PixelFormats.Bgra32, null, output, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Export(string inputPath, string outputPath, FixerConfig config, CalibrationData? calibration,
        double redGain, double greenGain, double blueGain, double exposure, double postContrast,
        double postSaturation = 1.0, double postBlackPoint = 0.0, double postWhitePoint = 1.0)
    {
        var frame = LoadBase(inputPath, config, calibration);
        var bitmap = Render(frame, redGain, greenGain, blueGain, exposure, postContrast,
            postSaturation, postBlackPoint, postWhitePoint);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var tempPath = outputPath + ".tmp";
        using (var stream = File.Create(tempPath)) encoder.Save(stream);
        File.Move(tempPath, outputPath, true);
    }

    public static byte[] ApplyAdjustments(byte[] input, double redGain, double greenGain, double blueGain,
        double exposure, double postContrast, double postSaturation = 1.0,
        double postBlackPoint = 0.0, double postWhitePoint = 1.0)
    {
        if (postWhitePoint <= postBlackPoint + 0.001)
            throw new ArgumentOutOfRangeException(nameof(postWhitePoint), "白位必须高于黑位。");
        var output = (byte[])input.Clone();
        var exposureFactor = Math.Pow(2.0, exposure);
        for (var i = 0; i < output.Length; i += 4)
        {
            var b = AdjustLevel(output[i] / 255.0 * blueGain * exposureFactor,
                postBlackPoint, postWhitePoint, postContrast);
            var g = AdjustLevel(output[i + 1] / 255.0 * greenGain * exposureFactor,
                postBlackPoint, postWhitePoint, postContrast);
            var r = AdjustLevel(output[i + 2] / 255.0 * redGain * exposureFactor,
                postBlackPoint, postWhitePoint, postContrast);
            var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            output[i] = ClampByte(luminance + (b - luminance) * postSaturation);
            output[i + 1] = ClampByte(luminance + (g - luminance) * postSaturation);
            output[i + 2] = ClampByte(luminance + (r - luminance) * postSaturation);
        }
        return output;
    }

    private static void ApplyBaseCorrection(byte[] pixels, FixerConfig config, CalibrationData? calibration)
    {
        ValidateCalibration(calibration);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (calibration?.Mode?.Equals("composite3d", StringComparison.OrdinalIgnoreCase) == true)
            {
                pixels[i] = (byte)calibration.Blue![pixels[i]];
                pixels[i + 1] = (byte)calibration.Green![pixels[i + 1]];
                pixels[i + 2] = (byte)calibration.Red![pixels[i + 2]];
                ApplyLut3D(pixels, i, calibration);
            }
            else if (calibration?.Mode?.Equals("lut3d", StringComparison.OrdinalIgnoreCase) == true)
            {
                ApplyLut3D(pixels, i, calibration);
            }
            else if (calibration?.Mode?.Equals("polynomial", StringComparison.OrdinalIgnoreCase) == true)
            {
                var rn = pixels[i + 2] / 255.0; var gn = pixels[i + 1] / 255.0; var bn = pixels[i] / 255.0;
                double[] terms = [1.0, rn, gn, bn, rn * rn, gn * gn, bn * bn, rn * gn, rn * bn, gn * bn];
                pixels[i] = ClampByte(Dot(calibration.Blue!, terms));
                pixels[i + 1] = ClampByte(Dot(calibration.Green!, terms));
                pixels[i + 2] = ClampByte(Dot(calibration.Red!, terms));
            }
            else if (calibration?.Mode?.Equals("luminance", StringComparison.OrdinalIgnoreCase) == true)
            {
                var sourceB = pixels[i]; var sourceG = pixels[i + 1]; var sourceR = pixels[i + 2];
                var sourceY = Math.Clamp((int)Math.Round(0.2126 * sourceR + 0.7152 * sourceG + 0.0722 * sourceB), 0, 255);
                var targetY = calibration.Luma![sourceY];
                var ratio = sourceY > 0 ? targetY / (double)sourceY : 0.0;
                pixels[i] = ClampByte((targetY + (sourceB * ratio - targetY) * calibration.Saturation) / 255.0);
                pixels[i + 1] = ClampByte((targetY + (sourceG * ratio - targetY) * calibration.Saturation) / 255.0);
                pixels[i + 2] = ClampByte((targetY + (sourceR * ratio - targetY) * calibration.Saturation) / 255.0);
            }
            else if (calibration is not null)
            {
                pixels[i] = (byte)calibration.Blue![pixels[i]];
                pixels[i + 1] = (byte)calibration.Green![pixels[i + 1]];
                pixels[i + 2] = (byte)calibration.Red![pixels[i + 2]];
            }
            else
            {
                var b = Tone(pixels[i] / 255.0, config);
                var g = Tone(pixels[i + 1] / 255.0, config);
                var r = Tone(pixels[i + 2] / 255.0, config);
                var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                pixels[i] = ClampByte(luminance + (b - luminance) * config.Saturation);
                pixels[i + 1] = ClampByte(luminance + (g - luminance) * config.Saturation);
                pixels[i + 2] = ClampByte(luminance + (r - luminance) * config.Saturation);
            }
        }
    }

    private static double Tone(double value, FixerConfig config)
    {
        value = Math.Pow(value, config.Gamma);
        value = (value - config.BlackPoint) / (config.WhitePoint - config.BlackPoint);
        return Math.Clamp((value - 0.5) * config.Contrast + 0.5, 0.0, 1.0);
    }

    private static byte ClampByte(double value) => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);

    private static double AdjustLevel(double value, double blackPoint, double whitePoint, double postContrast)
    {
        var adjusted = (value - blackPoint) / (whitePoint - blackPoint);
        adjusted = (adjusted - 0.5) * postContrast + 0.5;
        return adjusted;
    }

    public static (double R, double G, double B) EvaluateLut3D(
        double r, double g, double b, int gridSize, double[] lut)
    {
        var rf = Math.Clamp(r, 0.0, 1.0) * (gridSize - 1);
        var gf = Math.Clamp(g, 0.0, 1.0) * (gridSize - 1);
        var bf = Math.Clamp(b, 0.0, 1.0) * (gridSize - 1);
        var r0 = Math.Min((int)rf, gridSize - 2); var rt = rf - r0;
        var g0 = Math.Min((int)gf, gridSize - 2); var gt = gf - g0;
        var b0 = Math.Min((int)bf, gridSize - 2); var bt = bf - b0;
        double or = 0, og = 0, ob = 0;
        for (var dr = 0; dr <= 1; dr++)
        for (var dg = 0; dg <= 1; dg++)
        for (var db = 0; db <= 1; db++)
        {
            var weight = (dr == 0 ? 1 - rt : rt) * (dg == 0 ? 1 - gt : gt) * (db == 0 ? 1 - bt : bt);
            var index = (((r0 + dr) * gridSize + g0 + dg) * gridSize + b0 + db) * 3;
            or += lut[index] * weight;
            og += lut[index + 1] * weight;
            ob += lut[index + 2] * weight;
        }
        return (or, og, ob);
    }

    private static void ApplyLut3D(byte[] pixels, int offset, CalibrationData calibration)
    {
        var result = EvaluateLut3D(pixels[offset + 2] / 255.0, pixels[offset + 1] / 255.0,
            pixels[offset] / 255.0, calibration.GridSize, calibration.Lut3D!);
        pixels[offset] = ClampByte(result.B);
        pixels[offset + 1] = ClampByte(result.G);
        pixels[offset + 2] = ClampByte(result.R);
    }

    private static double Dot(double[] coefficients, double[] terms)
    {
        var result = 0.0;
        for (var i = 0; i < terms.Length; i++) result += coefficients[i] * terms[i];
        return result;
    }

    private static void ValidateCalibration(CalibrationData? calibration)
    {
        if (calibration is null) return;
        if (calibration.Mode?.Equals("composite3d", StringComparison.OrdinalIgnoreCase) == true)
        {
            ValidateRgbLut(calibration);
            ValidateLut3D(calibration);
            return;
        }
        if (calibration.Mode?.Equals("lut3d", StringComparison.OrdinalIgnoreCase) == true)
        {
            ValidateLut3D(calibration);
            return;
        }
        if (calibration.Mode?.Equals("luminance", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (calibration.Luma?.Length != 256) throw new InvalidDataException("亮度标定表必须包含 256 个值。");
            return;
        }
        if (calibration.Mode?.Equals("polynomial", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (calibration.Red?.Length != 10 || calibration.Green?.Length != 10 || calibration.Blue?.Length != 10)
                throw new InvalidDataException("多项式标定表每个通道必须包含 10 个系数。");
            return;
        }
        ValidateRgbLut(calibration);
    }

    private static void ValidateRgbLut(CalibrationData calibration)
    {
        if (calibration.Red?.Length != 256 || calibration.Green?.Length != 256 || calibration.Blue?.Length != 256)
            throw new InvalidDataException("RGB 标定表每个通道必须包含 256 个值。");
    }

    private static void ValidateLut3D(CalibrationData calibration)
    {
        if (calibration.GridSize is < 2 or > 65 ||
            calibration.Lut3D?.Length != calibration.GridSize * calibration.GridSize * calibration.GridSize * 3 ||
            calibration.Lut3D.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidDataException("3D LUT 标定数据无效。");
    }
}
