param(
    [string]$File,
    [string]$OutputPath,
    [string]$InputPath,
    [string]$CalibrationPath,
    [switch]$ProcessExisting
)

$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AmdHdrScreenshotFixer'
if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
}
$configPath = Join-Path $dataDirectory 'config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    $legacyConfigPath = Join-Path $PSScriptRoot 'config.json'
    if (Test-Path -LiteralPath $legacyConfigPath -PathType Leaf) {
        Copy-Item -LiteralPath $legacyConfigPath -Destination $configPath
    }
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Missing config file: $configPath"
}

$config = Get-Content -Raw -Encoding UTF8 -LiteralPath $configPath | ConvertFrom-Json
$logPath = Join-Path $dataDirectory 'fixer.log'

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Encoding UTF8 -LiteralPath $logPath -Value $line
}

$processorSource = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class AmdScreenshotProcessor
{
    private static byte ClampByte(double value)
    {
        if (value <= 0.0) return 0;
        if (value >= 1.0) return 255;
        return (byte)Math.Round(value * 255.0);
    }

    private static double Tone(double value, double gamma, double blackPoint,
        double whitePoint, double contrast)
    {
        value = Math.Pow(value, gamma);
        value = (value - blackPoint) / (whitePoint - blackPoint);
        value = (value - 0.5) * contrast + 0.5;
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    private static byte AdjustChannel(byte value, double gain, double exposureFactor, double postContrast)
    {
        double adjusted = value / 255.0 * gain * exposureFactor;
        adjusted = (adjusted - 0.5) * postContrast + 0.5;
        return ClampByte(adjusted);
    }

    public static void Process(string inputPath, string outputPath, double gamma,
        double blackPoint, double whitePoint, double contrast, double saturation,
        int[] redLut, int[] greenLut, int[] blueLut, int[] lumaLut, double calibratedSaturation,
        double[] redPolynomial, double[] greenPolynomial, double[] bluePolynomial,
        double redGain, double greenGain, double blueGain, double exposure, double postContrast)
    {
        if (whitePoint <= blackPoint)
            throw new ArgumentException("whitePoint must be greater than blackPoint");

        using (var source = new Bitmap(inputPath))
        using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        {
            if (source.HorizontalResolution > 0 && source.VerticalResolution > 0)
                bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bits = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int byteCount = Math.Abs(bits.Stride) * bitmap.Height;
                var pixels = new byte[byteCount];
                Marshal.Copy(bits.Scan0, pixels, 0, byteCount);

                double exposureFactor = Math.Pow(2.0, exposure);
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int row = y * Math.Abs(bits.Stride);
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int i = row + x * 4;
                        if (redPolynomial != null && greenPolynomial != null && bluePolynomial != null)
                        {
                            double rn = pixels[i + 2] / 255.0, gn = pixels[i + 1] / 255.0, bn = pixels[i] / 255.0;
                            double[] f = { 1.0, rn, gn, bn, rn * rn, gn * gn, bn * bn, rn * gn, rn * bn, gn * bn };
                            double rr = 0, gg = 0, bb = 0;
                            for (int j = 0; j < f.Length; j++) { rr += redPolynomial[j] * f[j]; gg += greenPolynomial[j] * f[j]; bb += bluePolynomial[j] * f[j]; }
                            pixels[i] = ClampByte(bb);
                            pixels[i + 1] = ClampByte(gg);
                            pixels[i + 2] = ClampByte(rr);
                        }
                        else if (lumaLut != null)
                        {
                            int sourceB = pixels[i], sourceG = pixels[i + 1], sourceR = pixels[i + 2];
                            int sourceY = Math.Max(0, Math.Min(255, (int)Math.Round(0.2126 * sourceR + 0.7152 * sourceG + 0.0722 * sourceB)));
                            double targetY = lumaLut[sourceY];
                            double ratio = sourceY > 0 ? targetY / sourceY : 0.0;
                            double baseB = sourceB * ratio, baseG = sourceG * ratio, baseR = sourceR * ratio;
                            pixels[i] = ClampByte((targetY + (baseB - targetY) * calibratedSaturation) / 255.0);
                            pixels[i + 1] = ClampByte((targetY + (baseG - targetY) * calibratedSaturation) / 255.0);
                            pixels[i + 2] = ClampByte((targetY + (baseR - targetY) * calibratedSaturation) / 255.0);
                        }
                        else if (redLut != null && greenLut != null && blueLut != null)
                        {
                            pixels[i] = (byte)blueLut[pixels[i]];
                            pixels[i + 1] = (byte)greenLut[pixels[i + 1]];
                            pixels[i + 2] = (byte)redLut[pixels[i + 2]];
                        }
                        else
                        {
                            double b = Tone(pixels[i] / 255.0, gamma, blackPoint, whitePoint, contrast);
                            double g = Tone(pixels[i + 1] / 255.0, gamma, blackPoint, whitePoint, contrast);
                            double r = Tone(pixels[i + 2] / 255.0, gamma, blackPoint, whitePoint, contrast);

                            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                            r = luminance + (r - luminance) * saturation;
                            g = luminance + (g - luminance) * saturation;
                            b = luminance + (b - luminance) * saturation;

                            pixels[i] = ClampByte(b);
                            pixels[i + 1] = ClampByte(g);
                            pixels[i + 2] = ClampByte(r);
                        }

                        pixels[i] = AdjustChannel(pixels[i], blueGain, exposureFactor, postContrast);
                        pixels[i + 1] = AdjustChannel(pixels[i + 1], greenGain, exposureFactor, postContrast);
                        pixels[i + 2] = AdjustChannel(pixels[i + 2], redGain, exposureFactor, postContrast);
                    }
                }

                Marshal.Copy(pixels, 0, bits.Scan0, byteCount);
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }

            string tempPath = outputPath + ".tmp.png";
            if (File.Exists(tempPath)) File.Delete(tempPath);
            bitmap.Save(tempPath, ImageFormat.Png);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }
    }
}
'@

if (-not ('AmdScreenshotProcessor' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition $processorSource -ReferencedAssemblies System.Drawing
}

function Get-FixedPath([IO.FileInfo]$InputFile) {
    if ($OutputPath) {
        return [IO.Path]::GetFullPath($OutputPath)
    }
    return Join-Path $InputFile.DirectoryName ($InputFile.BaseName + $config.suffix + $InputFile.Extension)
}

function Invoke-Fix([string]$InputFilePath) {
    $inputFile = Get-Item -LiteralPath $InputFilePath
    if ($inputFile.Extension -notmatch '^\.png$') {
        throw "Only PNG files are supported: $InputFilePath"
    }
    if ($inputFile.BaseName.EndsWith([string]$config.suffix, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $fixedPath = Get-FixedPath $inputFile
    $fixedDirectory = Split-Path -Parent $fixedPath
    if (-not (Test-Path -LiteralPath $fixedDirectory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $fixedDirectory | Out-Null
    }

    $redLut = $null
    $greenLut = $null
    $blueLut = $null
    $lumaLut = $null
    $calibratedSaturation = 1.0
    $redPolynomial = $null
    $greenPolynomial = $null
    $bluePolynomial = $null
    $selectedCalibrationPath = if ($CalibrationPath) {
        [IO.Path]::GetFullPath($CalibrationPath)
    }
    elseif ($config.calibrationFile) {
        $systemCalibrationPath = Join-Path $dataDirectory ([string]$config.calibrationFile)
        if (-not (Test-Path -LiteralPath $systemCalibrationPath -PathType Leaf)) {
            $legacyCalibrationPath = Join-Path $PSScriptRoot ([string]$config.calibrationFile)
            if (Test-Path -LiteralPath $legacyCalibrationPath -PathType Leaf) {
                Copy-Item -LiteralPath $legacyCalibrationPath -Destination $systemCalibrationPath
            }
        }
        $systemCalibrationPath
    }
    else {
        $null
    }
    if ($selectedCalibrationPath) {
        if (Test-Path -LiteralPath $selectedCalibrationPath -PathType Leaf) {
            $calibration = Get-Content -Raw -Encoding UTF8 -LiteralPath $selectedCalibrationPath | ConvertFrom-Json
            if ($calibration.mode -eq 'polynomial') {
                $redPolynomial = [double[]]$calibration.red
                $greenPolynomial = [double[]]$calibration.green
                $bluePolynomial = [double[]]$calibration.blue
                if ($redPolynomial.Count -ne 10 -or $greenPolynomial.Count -ne 10 -or $bluePolynomial.Count -ne 10) {
                    throw "Polynomial calibration must contain 10 coefficients per channel: $selectedCalibrationPath"
                }
            }
            elseif ($calibration.mode -eq 'luminance') {
                $lumaLut = [int[]]$calibration.luma
                $calibratedSaturation = [double]$calibration.saturation
                if ($lumaLut.Count -ne 256) {
                    throw "Luminance calibration LUT must contain 256 values: $selectedCalibrationPath"
                }
            }
            else {
                $redLut = [int[]]$calibration.red
                $greenLut = [int[]]$calibration.green
                $blueLut = [int[]]$calibration.blue
                if ($redLut.Count -ne 256 -or $greenLut.Count -ne 256 -or $blueLut.Count -ne 256) {
                    throw "Calibration LUT must contain 256 values per channel: $selectedCalibrationPath"
                }
            }
        }
    }

    $exposure = if ($null -ne $config.exposure) { [double]$config.exposure } else { 0.0 }
    $postContrast = if ($null -ne $config.postContrast) { [double]$config.postContrast } else { 1.0 }
    [AmdScreenshotProcessor]::Process(
        $inputFile.FullName,
        $fixedPath,
        [double]$config.gamma,
        [double]$config.blackPoint,
        [double]$config.whitePoint,
        [double]$config.contrast,
        [double]$config.saturation,
        $redLut,
        $greenLut,
        $blueLut,
        $lumaLut,
        $calibratedSaturation,
        $redPolynomial,
        $greenPolynomial,
        $bluePolynomial,
        [double]$config.redGain,
        [double]$config.greenGain,
        [double]$config.blueGain,
        $exposure,
        $postContrast
    )
    Write-Log "Fixed: $($inputFile.FullName) -> $fixedPath"
}

if ($File) {
    Invoke-Fix ([IO.Path]::GetFullPath($File))
    exit 0
}

$configuredInputPath = if ($InputPath) { $InputPath } else { [string]$config.inputPath }
$inputRoot = [IO.Path]::GetFullPath($configuredInputPath)
if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) {
    throw "Input directory does not exist: $inputRoot"
}

$known = @{}
if (-not $ProcessExisting) {
    Get-ChildItem -LiteralPath $inputRoot -Filter '*.png' -File -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $known[$_.FullName] = '{0}:{1}' -f $_.Length, $_.LastWriteTimeUtc.Ticks }
}

Write-Log "Watching: $inputRoot (press Ctrl+C to stop)"
while ($true) {
    Get-ChildItem -LiteralPath $inputRoot -Filter '*.png' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { -not $_.BaseName.EndsWith([string]$config.suffix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $signature = '{0}:{1}' -f $_.Length, $_.LastWriteTimeUtc.Ticks
            if (-not $known.ContainsKey($_.FullName) -or $known[$_.FullName] -ne $signature) {
                $known[$_.FullName] = $signature
                try {
                    Start-Sleep -Milliseconds 500
                    $current = Get-Item -LiteralPath $_.FullName
                    $currentSignature = '{0}:{1}' -f $current.Length, $current.LastWriteTimeUtc.Ticks
                    if ($currentSignature -eq $signature) {
                        Invoke-Fix $current.FullName
                    }
                }
                catch {
                    Write-Log "Error: $($_.Exception.Message)"
                }
            }
        }
    Start-Sleep -Milliseconds ([int]$config.pollIntervalMs)
}
