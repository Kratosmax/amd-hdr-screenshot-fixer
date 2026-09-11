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

var testDirectory = Path.Combine(Path.GetTempPath(), "amd-hdr-fixer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    File.WriteAllText(Path.Combine(testDirectory, "config.json"), "{\"unknownSetting\":42,\"redGain\":1}");
    var testStore = new ConfigStore(testDirectory);
    testStore.SaveAdjustments(0.9, 1.1, 1.2, 0.25, 1.15);
    testStore.SaveWatchPath(Path.Combine(testDirectory, "watched"));
    testStore.SaveSkippedVersion("0.2.0");
    var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(testDirectory, "config.json")))!.AsObject();
    if (saved["unknownSetting"]!.GetValue<int>() != 42 || saved["redGain"]!.GetValue<double>() != 0.9 ||
        saved["greenGain"]!.GetValue<double>() != 1.1 || saved["blueGain"]!.GetValue<double>() != 1.2 ||
        saved["exposure"]!.GetValue<double>() != 0.25 || saved["postContrast"]!.GetValue<double>() != 1.15 ||
        saved["watchPath"]!.GetValue<string>() != Path.Combine(testDirectory, "watched") ||
        saved["skippedVersion"]!.GetValue<string>() != "0.2.0")
        throw new InvalidOperationException("Config gain update did not preserve existing fields.");
    Console.WriteLine("PASS: config adjustment save preserves unrelated fields.");
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
        config.RedGain, config.GreenGain, config.BlueGain, config.Exposure, config.PostContrast);
    Console.WriteLine($"EXPORT: {args[2]}");
}

if (args.Length == 4 && args[0] == "--export-config")
{
    var store = new ConfigStore(args[1]);
    var config = store.Load();
    ImageProcessor.Export(args[2], args[3], config, store.LoadCalibration(config),
        config.RedGain, config.GreenGain, config.BlueGain, config.Exposure, config.PostContrast);
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
