using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace AmdHdrScreenshotFixer;

public sealed class ConfigStore
{
    private readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public ConfigStore(string? dataDirectory = null)
    {
        var useSystemStore = dataDirectory is null;
        DataDirectory = useSystemStore
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmdHdrScreenshotFixer")
            : Path.GetFullPath(dataDirectory!);
        Directory.CreateDirectory(DataDirectory);
        ConfigPath = Path.Combine(DataDirectory, "config.json");
        if (useSystemStore)
        {
            SeedFile("config.json", "AmdHdrScreenshotFixer.Defaults.config.json");
            SeedFile("calibration.json", "AmdHdrScreenshotFixer.Defaults.calibration.json");
        }
    }

    public string DataDirectory { get; }
    public string ConfigPath { get; }

    public FixerConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new FixerConfig();
        return JsonSerializer.Deserialize<FixerConfig>(File.ReadAllText(ConfigPath), options) ?? new FixerConfig();
    }

    public CalibrationData? LoadCalibration(FixerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CalibrationFile)) return null;
        var path = Path.Combine(DataDirectory, config.CalibrationFile);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(path), options);
    }

    public void SaveAdjustments(double red, double green, double blue, double exposure, double postContrast)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["redGain"] = Math.Round(red, 3);
        root["greenGain"] = Math.Round(green, 3);
        root["blueGain"] = Math.Round(blue, 3);
        root["exposure"] = Math.Round(exposure, 3);
        root["postContrast"] = Math.Round(postContrast, 3);
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    public void SaveWatchPath(string watchPath)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["watchPath"] = Path.GetFullPath(watchPath);
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    public void SaveSkippedVersion(string? version)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["skippedVersion"] = version;
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    private void SeedFile(string fileName, string resourceName)
    {
        var destination = Path.Combine(DataDirectory, fileName);
        if (File.Exists(destination)) return;

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.CurrentDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && !Path.GetFullPath(candidate).Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(candidate, destination);
                return;
            }
        }

        using var source = typeof(ConfigStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded default: {resourceName}");
        using var output = File.Create(destination);
        source.CopyTo(output);
    }
}
