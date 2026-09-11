using AmdHdrScreenshotFixer;
using AmdHdrScreenshotFixer.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json.Nodes;

var input = new byte[] { 100, 120, 140, 255 };
var output = ImageProcessor.ApplyAdjustments(input, 0.5, 1.0, 1.5, 0.0, 1.0);
var expected = new byte[] { 150, 120, 70, 255 };

if (!output.SequenceEqual(expected))
    throw new InvalidOperationException($"RGB gain mismatch. Actual: {string.Join(',', output)}");
if (!input.SequenceEqual(new byte[] { 100, 120, 140, 255 }))
    throw new InvalidOperationException("Input buffer was modified.");

Console.WriteLine("PASS: BGRA gains map to the correct RGB channels and preserve alpha/input.");

var exposureOutput = ImageProcessor.ApplyAdjustments(new byte[] { 64, 64, 64, 255 }, 1, 1, 1, 1.0, 1.0);
if (!exposureOutput.SequenceEqual(new byte[] { 128, 128, 128, 255 }))
    throw new InvalidOperationException("+1 EV did not double channel values.");
var contrastOutput = ImageProcessor.ApplyAdjustments(new byte[] { 64, 64, 64, 255 }, 1, 1, 1, 0.0, 0.5);
if (!contrastOutput.SequenceEqual(new byte[] { 96, 96, 96, 255 }))
    throw new InvalidOperationException("Contrast did not scale values around the 0.5 midpoint.");
Console.WriteLine("PASS: exposure and contrast use the expected neutral point and scale.");

var saturationOutput = ImageProcessor.ApplyAdjustments(new byte[] { 0, 0, 255, 255 }, 1, 1, 1, 0, 1, 0);
if (saturationOutput[0] != saturationOutput[1] || saturationOutput[1] != saturationOutput[2])
    throw new InvalidOperationException("Zero saturation did not produce a neutral pixel.");
var levelsOutput = ImageProcessor.ApplyAdjustments(new byte[] { 128, 128, 128, 255 }, 1, 1, 1, 0, 1, 1, 0.25, 0.75);
if (!levelsOutput.SequenceEqual(new byte[] { 128, 128, 128, 255 }))
    throw new InvalidOperationException($"Black/white levels mismatch. Actual: {string.Join(',', levelsOutput)}");
Console.WriteLine("PASS: saturation and black/white point adjustments use the expected pixel pipeline.");

var identityLut = new double[2 * 2 * 2 * 3];
for (var r = 0; r < 2; r++) for (var g = 0; g < 2; g++) for (var b = 0; b < 2; b++)
{
    var index = ((r * 2 + g) * 2 + b) * 3;
    identityLut[index] = r; identityLut[index + 1] = g; identityLut[index + 2] = b;
}
var interpolated = ImageProcessor.EvaluateLut3D(0.25, 0.5, 0.75, 2, identityLut);
if (Math.Abs(interpolated.R - 0.25) > 1e-9 || Math.Abs(interpolated.G - 0.5) > 1e-9 || Math.Abs(interpolated.B - 0.75) > 1e-9)
    throw new InvalidOperationException("3D LUT trilinear interpolation is incorrect.");
Console.WriteLine("PASS: 3D LUT uses trilinear interpolation with the documented RGB order.");

var normalizedNetwork = new UpdateNetworkSettings([
    new GithubProxySetting(string.Empty, 3, true),
    new GithubProxySetting("https://mirror.example/github/", 8),
    new GithubProxySetting("https://MIRROR.example/github", 7)
], "http://127.0.0.1:7890").Normalize();
if (normalizedNetwork.GithubProxies!.Count != 2 || normalizedNetwork.HttpProxy != "http://127.0.0.1:7890")
    throw new InvalidOperationException("Update network normalization failed.");
var routes = UpdateRouteBuilder.Build(new Uri("https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/latest/download/update.json"), normalizedNetwork);
if (routes.Count != 2 || routes[0].DisplayName != "mirror.example" || !routes[1].IsDirect)
    throw new InvalidOperationException("Update route priority or direct fallback is incorrect.");
if (UpdateNetworkSettings.TryNormalizeHttpProxy("https://127.0.0.1:7890", out _) ||
    UpdateNetworkSettings.TryNormalizeGithubProxy("https://user:secret@mirror.example/path", out _))
    throw new InvalidOperationException("Unsafe proxy input was accepted.");
Console.WriteLine("PASS: update proxy settings normalize, deduplicate and preserve stable priority fallback.");

var testDirectory = Path.Combine(Path.GetTempPath(), "amd-hdr-fixer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    File.WriteAllText(Path.Combine(testDirectory, "config.json"), "{\"unknownSetting\":42,\"redGain\":1}");
    var testStore = new ConfigStore(testDirectory);
    testStore.SaveAdjustments(0.9, 1.1, 1.2, 0.25, 1.15, 1.25, 0.02, 0.94);
    testStore.SaveWatchPath(Path.Combine(testDirectory, "watched"));
    testStore.SaveSkippedVersion("0.2.0");
    testStore.SaveApplicationSettings(false, false,
        new UpdateNetworkSettings([new GithubProxySetting(string.Empty, 10, true)], "http://127.0.0.1:7890"));
    var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(testDirectory, "config.json")))!.AsObject();
    if (saved["unknownSetting"]!.GetValue<int>() != 42 || saved["redGain"]!.GetValue<double>() != 0.9 ||
        saved["greenGain"]!.GetValue<double>() != 1.1 || saved["blueGain"]!.GetValue<double>() != 1.2 ||
        saved["exposure"]!.GetValue<double>() != 0.25 || saved["postContrast"]!.GetValue<double>() != 1.15 ||
        saved["postSaturation"]!.GetValue<double>() != 1.25 || saved["postBlackPoint"]!.GetValue<double>() != 0.02 ||
        saved["postWhitePoint"]!.GetValue<double>() != 0.94 || saved["autoUpdateEnabled"]!.GetValue<bool>() ||
        saved["watchPath"]!.GetValue<string>() != Path.Combine(testDirectory, "watched") ||
        saved["skippedVersion"]!.GetValue<string>() != "0.2.0")
        throw new InvalidOperationException("Config gain update did not preserve existing fields.");
    Console.WriteLine("PASS: config adjustment save preserves unrelated fields.");

    var calibrationPairs = new List<CalibrationPair>();
    for (var pairIndex = 0; pairIndex < 3; pairIndex++)
    {
        var amdPath = Path.Combine(testDirectory, $"amd-{pairIndex}.png");
        var referencePath = Path.Combine(testDirectory, $"reference-{pairIndex}.png");
        WriteGradientPng(amdPath, pairIndex);
        File.Copy(amdPath, referencePath);
        calibrationPairs.Add(new CalibrationPair(amdPath, referencePath));
    }
    var identity = Enumerable.Range(0, 256).Select(value => (double)value).ToArray();
    var fitted = CalibrationEngine.Fit(calibrationPairs, new CalibrationData
    {
        Mode = "histogram", Red = identity, Green = identity, Blue = identity
    });
    Console.WriteLine($"CALIBRATION: score={fitted.Quality.Score:F1}, mean={fitted.Quality.MeanDeltaE:F3}, p95={fitted.Quality.P95DeltaE:F3}, grid={fitted.GridSize}");
    if (!fitted.Quality.MeetsTarget || fitted.Quality.MeanDeltaE > 3.0 || fitted.Calibration.Mode != "lut3d" ||
        fitted.Calibration.Lut3D?.Length != fitted.GridSize * fitted.GridSize * fitted.GridSize * 3)
        throw new InvalidOperationException("Offline cross-validation calibration did not fit identity pairs.");
    testStore.SaveCalibration(new FixerConfig(), fitted.Calibration);
    if (testStore.LoadCalibration(new FixerConfig())?.Mode != "lut3d")
        throw new InvalidOperationException("3D calibration did not persist and reload.");
    Console.WriteLine("PASS: three-pair offline calibration cross-validates and persists a bounded 3D LUT.");

    var transformedPairs = new List<CalibrationPair>();
    for (var pairIndex = 0; pairIndex < 3; pairIndex++)
    {
        var amdPath = Path.Combine(testDirectory, $"transformed-amd-{pairIndex}.png");
        var referencePath = Path.Combine(testDirectory, $"transformed-reference-{pairIndex}.png");
        WriteTransformedPair(amdPath, referencePath, pairIndex);
        transformedPairs.Add(new CalibrationPair(amdPath, referencePath));
    }
    var transformedFit = CalibrationEngine.Fit(transformedPairs, new CalibrationData
    {
        Mode = "histogram", Red = identity, Green = identity, Blue = identity
    });
    var heldOutAmd = Path.Combine(testDirectory, "transformed-amd-held-out.png");
    var heldOutReference = Path.Combine(testDirectory, "transformed-reference-held-out.png");
    WriteTransformedPair(heldOutAmd, heldOutReference, 7);
    var rawFrame = ImageProcessor.LoadBase(heldOutAmd, new FixerConfig(), null);
    var correctedFrame = ImageProcessor.LoadBase(heldOutAmd, new FixerConfig(), transformedFit.Calibration);
    var referenceFrame = ImageProcessor.LoadBase(heldOutReference, new FixerConfig(), null);
    var rawMae = PixelMae(rawFrame.Pixels, referenceFrame.Pixels);
    var correctedMae = PixelMae(correctedFrame.Pixels, referenceFrame.Pixels);
    Console.WriteLine($"CALIBRATION HELD-OUT: raw MAE={rawMae:F3}, corrected MAE={correctedMae:F3}, " +
        $"mean DeltaE={transformedFit.Quality.MeanDeltaE:F3}, p95 DeltaE={transformedFit.Quality.P95DeltaE:F3}");
    if (!transformedFit.Quality.MeetsTarget || correctedMae >= rawMae * 0.5 || correctedMae > 6.0)
        throw new InvalidOperationException("Offline calibration did not generalize to a held-out transformed image.");
    Console.WriteLine("PASS: offline calibration corrects a known color distortion on an unseen image.");
}

finally
{
    Directory.Delete(testDirectory, true);
}

foreach (var unsafePath in new[] { "../escape.exe", "/absolute.exe", "C:/drive.exe", ".amd-hdr-screenshot-fixer-update/file" })
{
    try
    {
        _ = UpdatePackageValidator.NormalizeRelativePath(unsafePath);
        throw new InvalidOperationException($"Unsafe update path was accepted: {unsafePath}");
    }
    catch (InvalidDataException) { }
}
if (UpdatePackageValidator.NormalizeRelativePath("folder/app.dll") != "folder/app.dll")
    throw new InvalidOperationException("Safe update path normalization failed.");
Console.WriteLine("PASS: update package path validation rejects traversal and reserved paths.");

if (args.Length == 3 && args[0] == "--export")
{
    var store = new ConfigStore();
    var config = store.Load();
    ImageProcessor.Export(args[1], args[2], config, store.LoadCalibration(config),
        config.RedGain, config.GreenGain, config.BlueGain, config.Exposure, config.PostContrast,
        config.PostSaturation, config.PostBlackPoint, config.PostWhitePoint);
    Console.WriteLine($"EXPORT: {args[2]}");
}

if (args.Length == 4 && args[0] == "--export-config")
{
    var store = new ConfigStore(args[1]);
    var config = store.Load();
    ImageProcessor.Export(args[2], args[3], config, store.LoadCalibration(config),
        config.RedGain, config.GreenGain, config.BlueGain, config.Exposure, config.PostContrast,
        config.PostSaturation, config.PostBlackPoint, config.PostWhitePoint);
    Console.WriteLine($"EXPORT: {args[3]}");
}

if (args.Length == 4 && args[0] == "--watch-test")
{
    var watchDirectory = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(watchDirectory);
    var sourcePath = Path.GetFullPath(args[3]);
    var incomingPath = Path.Combine(watchDirectory, Path.GetFileName(sourcePath));
    var outputPath = Path.Combine(watchDirectory, "fixed", Path.GetFileName(sourcePath));
    using var watcher = new ScreenshotWatcher(new ConfigStore(args[1]));
    WatcherProcessingResult? completed = null;
    watcher.ProcessingCompleted += result => completed = result;
    watcher.Start(watchDirectory);
    File.Copy(sourcePath, incomingPath);

    for (var attempt = 0; attempt < 80 && !File.Exists(outputPath); attempt++)
        await Task.Delay(250);
    if (!File.Exists(outputPath)) throw new TimeoutException("Watcher did not create the fixed output.");
    for (var attempt = 0; attempt < 20 && completed is null; attempt++)
        await Task.Delay(100);
    if (completed?.SourcePath != incomingPath || completed.OutputPath != outputPath)
        throw new InvalidOperationException("Watcher completion event did not contain the expected paths.");
    await Task.Delay(1000);
    if (Directory.Exists(Path.Combine(watchDirectory, "fixed", "fixed")))
        throw new InvalidOperationException("Watcher recursively processed its own output.");
    watcher.Stop();
    Console.WriteLine($"PASS: watcher created fixed\\{Path.GetFileName(sourcePath)} without recursive output.");
}

if (args.Length == 2)
{
    var first = ReadPixels(args[0]);
    var second = ReadPixels(args[1]);
    if (first.Length != second.Length) throw new InvalidOperationException("Compared images have different dimensions.");
    long error = 0;
    var maxError = 0;
    for (var i = 0; i < first.Length; i++)
    {
        var difference = Math.Abs(first[i] - second[i]);
        error += difference;
        maxError = Math.Max(maxError, difference);
    }
    var mae = error / (double)first.Length;
    Console.WriteLine($"COMPARE: byte MAE={mae:F6}, max={maxError}");
    if (maxError != 0) throw new InvalidOperationException("GUI and PowerShell outputs differ.");
}

static byte[] ReadPixels(string path)
{
    using var stream = File.OpenRead(path);
    var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
    return pixels;
}

static void WriteGradientPng(string path, int offset)
{
    const int width = 24, height = 24;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var index = (y * width + x) * 4;
        pixels[index] = (byte)((x * 11 + offset * 17) % 256);
        pixels[index + 1] = (byte)((y * 11 + offset * 29) % 256);
        pixels[index + 2] = (byte)(((x + y) * 7 + offset * 37) % 256);
        pixels[index + 3] = 255;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path); encoder.Save(stream);
}

static void WriteTransformedPair(string amdPath, string referencePath, int offset)
{
    const int width = 72, height = 72;
    var amdPixels = new byte[width * height * 4];
    var referencePixels = new byte[amdPixels.Length];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var index = (y * width + x) * 4;
        var r = ((x * 17 + y * 13 + offset * 47) & 255) / 255.0;
        var g = ((x * 7 + y * 19 + offset * 71) & 255) / 255.0;
        var b = ((x * 23 + y * 5 + offset * 31) & 255) / 255.0;
        amdPixels[index] = ToByte(b);
        amdPixels[index + 1] = ToByte(g);
        amdPixels[index + 2] = ToByte(r);
        amdPixels[index + 3] = 255;
        referencePixels[index] = ToByte(Math.Pow(b, 0.98) * 0.90 + r * 0.04 + g * 0.03);
        referencePixels[index + 1] = ToByte(Math.Pow(g, 1.04) * 0.94 + r * 0.03);
        referencePixels[index + 2] = ToByte(Math.Pow(r, 0.93) * 0.92 + g * 0.05 + 0.01);
        referencePixels[index + 3] = 255;
    }
    WritePixels(amdPath, width, height, amdPixels);
    WritePixels(referencePath, width, height, referencePixels);
}

static void WritePixels(string path, int width, int height, byte[] pixels)
{
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

static double PixelMae(byte[] actual, byte[] expected)
{
    long total = 0;
    var channelCount = actual.Length / 4 * 3;
    for (var index = 0; index < actual.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            total += Math.Abs(actual[index + channel] - expected[index + channel]);
    return total / (double)channelCount;
}
