namespace AmdHdrScreenshotFixer;

public sealed class FixerConfig
{
    public string Suffix { get; set; } = "_fixed";
    public string? CalibrationFile { get; set; } = "calibration.json";
    public string? WatchPath { get; set; }
    public string? SkippedVersion { get; set; }
    public double RedGain { get; set; } = 1.0;
    public double GreenGain { get; set; } = 1.0;
    public double BlueGain { get; set; } = 1.0;
    public double Exposure { get; set; }
    public double PostContrast { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;
    public double BlackPoint { get; set; }
    public double WhitePoint { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
}

public sealed class CalibrationData
{
    public string? Mode { get; set; }
    public double[]? Red { get; set; }
    public double[]? Green { get; set; }
    public double[]? Blue { get; set; }
    public int[]? Luma { get; set; }
    public double Saturation { get; set; } = 1.0;
}

public sealed record PixelFrame(int Width, int Height, double DpiX, double DpiY, byte[] Pixels, int Stride);

public sealed record WatcherProcessingResult(string SourcePath, string OutputPath);
